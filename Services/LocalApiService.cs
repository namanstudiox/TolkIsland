using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TolkIsland.Services;

public sealed record IslandActivity(string Title, string Body, int DurationSeconds);

/// <summary>
/// A tiny loopback-only HTTP surface for local scripts. It binds 127.0.0.1,
/// caps request bodies and concurrency, and never returns file contents or
/// settings. Every request except /health must carry the per-install token so
/// a web page cannot drive the island through the browser.
/// </summary>
public sealed class LocalApiService : IDisposable
{
    public const int DefaultPort = 5178;
    public const string DefaultBaseUrl = "http://127.0.0.1:5178/";
    public const string TokenHeader = "X-Tolk-Token";

    private const int MaxBodyBytes = 8192;
    private const int MaxConcurrentRequests = 8;

    private readonly object _sync = new();
    private readonly CancellationTokenSource _cancel = new();
    private readonly SemaphoreSlim _slots = new(MaxConcurrentRequests, MaxConcurrentRequests);
    private readonly int _port;
    private readonly Stopwatch _uptime = new();

    private HttpListener? _listener;
    private Task? _loop;
    private volatile bool _disposed;

    public LocalApiService(int port = DefaultPort, string? token = null)
    {
        _port = port is > 0 and < 65536 ? port : DefaultPort;
        BaseUrl = $"http://127.0.0.1:{_port}/";
        Token = string.IsNullOrWhiteSpace(token) ? LoadOrCreateToken() : token;
    }

    public string BaseUrl { get; }

    public int Port => _port;

    /// <summary>Required in the X-Tolk-Token header. Copied from Settings → Advanced.</summary>
    public string Token { get; }

    /// <summary>True while the user has paused the island; commands are refused.</summary>
    public Func<bool>? PausedProvider { get; set; }

    /// <summary>Maps a feature key ("media", "focus", "shelf", ...) to its setting.</summary>
    public Func<string, bool>? FeatureProvider { get; set; }

    public bool IsRunning
    {
        get
        {
            lock (_sync)
            {
                return _listener?.IsListening == true;
            }
        }
    }

    public event Action<IslandActivity>? ActivityReceived;

    public event Action<string>? ShelfRequested;

    public event Action<string>? PanelRequested;

    public event Action<string, int>? MediaCommandRequested;

    public event Action<int>? FocusRequested;

    public void Start()
    {
        lock (_sync)
        {
            if (_disposed || _listener is not null)
            {
                return;
            }

            try
            {
                var listener = new HttpListener();
                listener.Prefixes.Add(BaseUrl);
                listener.Start();
                _listener = listener;
                _uptime.Restart();
                _loop = Task.Run(() => LoopAsync(listener, _cancel.Token));
                DiagnosticsService.WriteLine("api", $"listening on {BaseUrl}");
            }
            catch (Exception exception)
            {
                // URL ACLs or another process can own the port. The rest of the
                // island keeps working without the local API.
                _listener = null;
                DiagnosticsService.Write("api.start", exception);
            }
        }
    }

    private async Task LoopAsync(HttpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (HttpListenerException)
            {
                return;
            }
            catch
            {
                continue;
            }

            _ = Task.Run(() => HandleAsync(context, token), token);
        }
    }

    private async Task HandleAsync(HttpListenerContext context, CancellationToken token)
    {
        var request = context.Request;
        var response = context.Response;
        var method = request.HttpMethod;
        var path = request.Url?.AbsolutePath ?? "/";
        var status = 500;

        if (!_slots.Wait(0))
        {
            status = 429;
            await WriteErrorAsync(
                response, status, "busy",
                "Too many requests are in flight. Retry shortly.", token).ConfigureAwait(false);
            Log(method, path, status);
            return;
        }

        try
        {
            response.ContentType = "application/json; charset=utf-8";
            response.Headers["Cache-Control"] = "no-store";

            var (handled, code) = await RouteAsync(request, response, path, token).ConfigureAwait(false);
            status = code;

            if (!handled)
            {
                status = 404;
                await WriteErrorAsync(response, status, "not_found", "No route matches this request.", token)
                    .ConfigureAwait(false);
            }
        }
        catch (JsonException)
        {
            status = 400;
            await TryWriteErrorAsync(
                response, status, "bad_json",
                "The request body must be a JSON object.", token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            status = 503;
        }
        catch (Exception exception)
        {
            status = 500;
            DiagnosticsService.Write("api.handle", exception);
            await TryWriteErrorAsync(
                response, status, "internal",
                "The island could not complete that request.", token).ConfigureAwait(false);
        }
        finally
        {
            _slots.Release();
            TryClose(response);
            Log(method, path, status);
        }
    }

    /// <summary>Returns whether the route matched, plus the status to log.</summary>
    private async Task<(bool Handled, int Status)> RouteAsync(
        HttpListenerRequest request,
        HttpListenerResponse response,
        string path,
        CancellationToken token)
    {
        if (request.HttpMethod == "GET" && path == "/health")
        {
            await WriteJsonAsync(response, 200, StatusPayload(), token).ConfigureAwait(false);
            return (true, 200);
        }

        if (!IsAuthorized(request))
        {
            await WriteErrorAsync(
                response, 401, "unauthorized",
                $"Send the token from Settings → Advanced in the {TokenHeader} header.",
                token).ConfigureAwait(false);
            return (true, 401);
        }

        if (request.HttpMethod == "GET" && path == "/status")
        {
            await WriteJsonAsync(response, 200, new
            {
                app = "TolkIsland",
                version = AppInfo.Version,
                apiVersion = AppInfo.ApiVersion,
                uptimeSeconds = (long)_uptime.Elapsed.TotalSeconds,
                paused = PausedProvider?.Invoke() == true,
                time = DateTimeOffset.Now
            }, token).ConfigureAwait(false);
            return (true, 200);
        }

        if (request.HttpMethod != "POST")
        {
            return (false, 404);
        }

        var (body, bodyError) = await ReadBodyAsync(request, token).ConfigureAwait(false);
        if (bodyError is not null)
        {
            await WriteErrorAsync(response, 413, bodyError, "Request bodies are limited to 8 KB.", token)
                .ConfigureAwait(false);
            return (true, 413);
        }

        switch (path)
        {
            case "/activity":
                return await HandleActivityAsync(response, body, token).ConfigureAwait(false);
            case "/focus":
                return await HandleFocusAsync(response, body, token).ConfigureAwait(false);
            case "/media":
                return await HandleMediaAsync(response, body, token).ConfigureAwait(false);
            case "/show":
                return await HandleShowAsync(response, body, token).ConfigureAwait(false);
            case "/shelf":
                return await HandleShelfAsync(response, body, token).ConfigureAwait(false);
            default:
                return (false, 404);
        }
    }

    private async Task<(bool, int)> HandleActivityAsync(
        HttpListenerResponse response,
        string body,
        CancellationToken token)
    {
        using var document = ParseObject(body);
        var root = document.RootElement;
        var title = GetString(root, "title", 120);
        var text = GetString(root, "body", 600);
        var seconds = GetInt(root, "seconds", 5, 1, 60);

        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(text))
        {
            await WriteErrorAsync(
                response, 400, "title_required", "Provide a title or body for the activity.", token)
                .ConfigureAwait(false);
            return (true, 400);
        }

        var gate = Evaluate("notifications");
        if (gate is not CommandDisposition.Accepted)
        {
            return (true, await WriteGateAsync(response, gate, token).ConfigureAwait(false));
        }

        ActivityReceived?.Invoke(new IslandActivity(
            string.IsNullOrWhiteSpace(title) ? "Local activity" : title,
            text,
            seconds));

        await WriteJsonAsync(response, 202, new { ok = true }, token).ConfigureAwait(false);
        return (true, 202);
    }

    private async Task<(bool, int)> HandleFocusAsync(
        HttpListenerResponse response,
        string body,
        CancellationToken token)
    {
        using var document = ParseObject(body);
        var stopwatch = document.RootElement.TryGetProperty("stopwatch", out var sw)
            && sw.ValueKind == JsonValueKind.True;
        var minutes = GetInt(document.RootElement, "minutes", 25, 1, 180);

        var gate = Evaluate("focus");
        if (gate is not CommandDisposition.Accepted)
        {
            return (true, await WriteGateAsync(response, gate, token).ConfigureAwait(false));
        }

        FocusRequested?.Invoke(stopwatch ? 0 : minutes * 60);
        await WriteJsonAsync(response, 202, new { ok = true }, token).ConfigureAwait(false);
        return (true, 202);
    }

    private async Task<(bool, int)> HandleMediaAsync(
        HttpListenerResponse response,
        string body,
        CancellationToken token)
    {
        using var document = ParseObject(body);
        var action = GetString(document.RootElement, "action", 32).ToLowerInvariant();
        if (action is not ("playpause" or "next" or "previous" or "seek"))
        {
            await WriteErrorAsync(
                response, 400, "unknown_action",
                "Action must be playpause, next, previous or seek.", token).ConfigureAwait(false);
            return (true, 400);
        }

        var gate = Evaluate("media");
        if (gate is not CommandDisposition.Accepted)
        {
            return (true, await WriteGateAsync(response, gate, token).ConfigureAwait(false));
        }

        var position = GetInt(document.RootElement, "position", 0, 0, 24 * 60 * 60);
        MediaCommandRequested?.Invoke(action, position);
        await WriteJsonAsync(response, 202, new { ok = true }, token).ConfigureAwait(false);
        return (true, 202);
    }

    private async Task<(bool, int)> HandleShowAsync(
        HttpListenerResponse response,
        string body,
        CancellationToken token)
    {
        using var document = ParseObject(body);
        var panel = GetString(document.RootElement, "panel", 32).ToLowerInvariant();
        if (panel is not ("shelf" or "notes" or "media" or "idle" or "focus"))
        {
            await WriteErrorAsync(
                response, 400, "unknown_panel",
                "Panel must be shelf, notes, media, idle or focus.", token)
                .ConfigureAwait(false);
            return (true, 400);
        }

        var feature = panel switch
        {
            "shelf" => "shelf",
            "media" => "media",
            "focus" => "focus",
            _ => string.Empty
        };

        var gate = Evaluate(feature);
        if (gate is not CommandDisposition.Accepted)
        {
            return (true, await WriteGateAsync(response, gate, token).ConfigureAwait(false));
        }

        PanelRequested?.Invoke(panel);
        await WriteJsonAsync(response, 202, new { ok = true }, token).ConfigureAwait(false);
        return (true, 202);
    }

    private async Task<(bool, int)> HandleShelfAsync(
        HttpListenerResponse response,
        string body,
        CancellationToken token)
    {
        using var document = ParseObject(body);
        var path = GetString(document.RootElement, "path", 1024);
        if (string.IsNullOrWhiteSpace(path))
        {
            await WriteErrorAsync(
                response, 400, "path_required", "Provide the path to place on the shelf.", token)
                .ConfigureAwait(false);
            return (true, 400);
        }

        var gate = Evaluate("shelf");
        if (gate is not CommandDisposition.Accepted)
        {
            return (true, await WriteGateAsync(response, gate, token).ConfigureAwait(false));
        }

        ShelfRequested?.Invoke(path);
        await WriteJsonAsync(response, 202, new { ok = true }, token).ConfigureAwait(false);
        return (true, 202);
    }

    private CommandDisposition Evaluate(string feature)
    {
        var enabled = feature.Length == 0 || FeatureProvider?.Invoke(feature) != false;
        return IslandCommandPolicy.Evaluate(PausedProvider?.Invoke() == true, enabled, knownCommand: true);
    }

    private static async Task<int> WriteGateAsync(
        HttpListenerResponse response,
        CommandDisposition disposition,
        CancellationToken token)
    {
        if (disposition == CommandDisposition.IgnoredPaused)
        {
            await WriteErrorAsync(
                response, 409, "paused",
                "The island is paused. Resume it before sending commands.", token).ConfigureAwait(false);
            return 409;
        }

        await WriteErrorAsync(
            response, 403, "disabled",
            "That feature is turned off in Tolk Island settings.", token).ConfigureAwait(false);
        return 403;
    }

    private object StatusPayload() => new
    {
        ok = true,
        app = "TolkIsland",
        version = AppInfo.Version,
        apiVersion = AppInfo.ApiVersion,
        uptimeSeconds = (long)_uptime.Elapsed.TotalSeconds,
        paused = PausedProvider?.Invoke() == true
    };

    private bool IsAuthorized(HttpListenerRequest request)
    {
        // Browsers attach Origin to cross-origin calls. The API never needs it,
        // and refusing it keeps a web page from reaching the routes at all.
        if (request.Headers["Origin"] is not null)
        {
            return false;
        }

        // Reject a mismatched Host header so DNS rebinding cannot redirect a
        // page's request to the loopback listener.
        if (!string.Equals(
                request.UserHostName,
                $"127.0.0.1:{_port}",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var provided = request.Headers[TokenHeader];
        return !string.IsNullOrEmpty(provided)
            && CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(provided),
                Encoding.UTF8.GetBytes(Token));
    }

    private static string LoadOrCreateToken()
    {
        try
        {
            var directory = SettingsService.DataDirectory;
            var path = Path.Combine(directory, "api-token.txt");
            if (File.Exists(path))
            {
                var existing = File.ReadAllText(path).Trim();
                if (existing.Length >= 32)
                {
                    return existing;
                }
            }

            var created = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
            Directory.CreateDirectory(directory);
            File.WriteAllText(path, created);
            return created;
        }
        catch
        {
            // An unwritable profile folder still gets a token for this run; the
            // Settings window shows whatever the live instance is using.
            return Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        }
    }

    private static JsonDocument ParseObject(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return JsonDocument.Parse("{}");
        }

        return JsonDocument.Parse(body);
    }

    private static async Task<(string Body, string? Error)> ReadBodyAsync(
        HttpListenerRequest request,
        CancellationToken token)
    {
        if (request.ContentLength64 > MaxBodyBytes)
        {
            return (string.Empty, "body_too_large");
        }

        var buffer = new byte[MaxBodyBytes + 1];
        var total = 0;

        using var stream = request.InputStream;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), token)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        if (total > MaxBodyBytes)
        {
            return (string.Empty, "body_too_large");
        }

        return (Encoding.UTF8.GetString(buffer, 0, total), null);
    }

    private static string GetString(JsonElement root, string name, int maxLength)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.String)
        {
            return string.Empty;
        }

        var text = value.GetString()?.Trim() ?? string.Empty;
        return text.Length <= maxLength ? text : text[..maxLength];
    }

    private static int GetInt(JsonElement root, string name, int fallback, int min, int max)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty(name, out var value)
            || !value.TryGetInt32(out var number))
        {
            return fallback;
        }

        return Math.Clamp(number, min, max);
    }

    private static async Task WriteJsonAsync(
        HttpListenerResponse response,
        int status,
        object payload,
        CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        response.StatusCode = status;
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, token).ConfigureAwait(false);
        response.Close();
    }

    private static Task WriteErrorAsync(
        HttpListenerResponse response,
        int status,
        string code,
        string message,
        CancellationToken token)
    {
        return WriteJsonAsync(response, status, new { ok = false, code, message }, token);
    }

    private static async Task TryWriteErrorAsync(
        HttpListenerResponse response,
        int status,
        string code,
        string message,
        CancellationToken token)
    {
        try
        {
            await WriteErrorAsync(response, status, code, message, token).ConfigureAwait(false);
        }
        catch
        {
            // Client disconnected before the error envelope was written.
        }
    }

    private static void TryClose(HttpListenerResponse response)
    {
        try
        {
            response.Close();
        }
        catch
        {
            // Client disconnected.
        }
    }

    private static void Log(string method, string path, int status)
    {
        DiagnosticsService.WriteLine("api", $"{method} {path} -> {status}");
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                _cancel.Cancel();
                _listener?.Stop();
                _listener?.Close();
            }
            catch
            {
                // Listener already stopped.
            }

            _listener = null;
            _loop = null;
        }

        _uptime.Stop();
        _cancel.Dispose();
        _slots.Dispose();
    }
}
