using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TolkIsland.Services;

public static class PromptService
{
    private const int MaxRequestChars = 8_000;
    private const int MaxResponseBytes = 256 * 1024;
    private const int MaxAnswerChars = 4_000;

    private static readonly Uri OfficialGroqEndpoint =
        new("https://api.groq.com/openai/v1/chat/completions");

    // Redirects are handled manually so a private endpoint cannot bounce out.
    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        AllowAutoRedirect = false
    })
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    internal static Uri? GroqEndpointOverride { get; set; }

    private static Uri GroqEndpoint => GroqEndpointOverride ?? OfficialGroqEndpoint;

    public static async Task<PromptResult> RunAsync(string prompt, CancellationToken token = default)
    {
        var text = prompt?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            return new PromptResult(PromptState.Succeeded, string.Empty);
        }

        if (text.Length > MaxRequestChars)
        {
            text = text[..MaxRequestChars];
        }

        return SettingsService.Current.GroqEnabled
            ? await RunGroqAsync(text, SettingsService.Current, token)
            : await RunLocalAsync(text, SettingsService.Current, token);
    }

    private static async Task<PromptResult> RunGroqAsync(
        string text,
        AppSettings settings,
        CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(settings.GroqApiKeyProtected))
        {
            return new PromptResult(
                PromptState.Failed,
                "Add a Groq API key in Settings → Advanced, or turn Groq off.");
        }

        var key = SecretProtection.Unprotect(settings.GroqApiKeyProtected)?.Trim() ?? string.Empty;
        if (key.Length == 0)
        {
            return new PromptResult(
                PromptState.Failed,
                "The saved Groq key cannot be read on this Windows account. Enter it again in Settings.");
        }

        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                model = string.IsNullOrWhiteSpace(settings.GroqModel)
                    ? AppSettings.DefaultGroqModel
                    : settings.GroqModel,
                messages = new[]
                {
                    new { role = "user", content = text }
                },
                stream = false
            });

            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var request = new HttpRequestMessage(HttpMethod.Post, GroqEndpoint)
            {
                Content = content
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            request.Headers.UserAgent.ParseAdd($"TolkIsland/{AppInfo.Version}");

            using var response = await Http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                token);

            if (!response.IsSuccessStatusCode)
            {
                return (int)response.StatusCode switch
                {
                    401 or 403 => new PromptResult(
                        PromptState.Failed,
                        "Groq rejected the API key. Update it in Settings."),
                    404 => new PromptResult(
                        PromptState.Failed,
                        "Groq does not recognise that model. Check the model name in Settings."),
                    429 => new PromptResult(
                        PromptState.Failed,
                        "Groq is rate-limiting this key. Try again in a moment."),
                    _ => new PromptResult(
                        PromptState.Failed,
                        $"Groq returned {(int)response.StatusCode}.")
                };
            }

            if (response.Content.Headers.ContentLength > MaxResponseBytes)
            {
                return new PromptResult(PromptState.Failed, "The Groq answer was too large.");
            }

            var body = await ReadBoundedAsync(response, token);
            if (body is null)
            {
                return new PromptResult(PromptState.Failed, "The Groq answer was too large.");
            }

            var answer = ExtractGroqAnswer(body);
            return answer.Length == 0
                ? new PromptResult(PromptState.Failed, "Groq sent an answer in an unexpected shape.")
                : new PromptResult(PromptState.Succeeded, answer);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return new PromptResult(PromptState.Cancelled, "Cancelled.");
        }
        catch (TaskCanceledException)
        {
            return new PromptResult(PromptState.Failed, "Groq took too long to answer.");
        }
        catch (HttpRequestException)
        {
            return new PromptResult(
                PromptState.Failed,
                "Groq is unreachable. Check your connection and try again.");
        }
        catch (Exception exception)
        {
            DiagnosticsService.Write("Prompt", exception);
            return new PromptResult(PromptState.Failed, "The Groq prompt failed.");
        }
    }

    private static async Task<PromptResult> RunLocalAsync(
        string text,
        AppSettings settings,
        CancellationToken token)
    {
        var endpoint = settings.LocalAiEndpoint?.Trim() ?? string.Empty;
        if (endpoint.Length > 0 && !endpoint.Contains("://", StringComparison.Ordinal))
        {
            endpoint = "http://" + endpoint;
        }

        if (endpoint.Length == 0)
        {
            try
            {
                System.Windows.Clipboard.SetText(text);
                return new PromptResult(
                    PromptState.Succeeded,
                    "Copied. Set an AI provider in settings to answer here.");
            }
            catch
            {
                return new PromptResult(PromptState.Failed, "Could not copy the prompt.");
            }
        }

        if (!SafeUrl.IsLoopbackOrPrivate(endpoint, out var endpointUri) || endpointUri is null)
        {
            return new PromptResult(
                PromptState.Failed,
                "The local AI address must point at this PC or your own network. Update it in settings.");
        }

        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                model = string.IsNullOrWhiteSpace(settings.LocalAiModel)
                    ? "llama3.2"
                    : settings.LocalAiModel,
                prompt = text,
                stream = false
            });

            using var response = await SendLocalAsync(endpointUri, payload, token);
            if (response is null)
            {
                return new PromptResult(
                    PromptState.Failed,
                    "The local AI request was blocked because the address or a redirect left your network. The prompt was not sent.");
            }

            if (!response.IsSuccessStatusCode)
            {
                return new PromptResult(
                    PromptState.Failed,
                    $"Local AI returned {(int)response.StatusCode}.");
            }

            if (response.Content.Headers.ContentLength > MaxResponseBytes)
            {
                return new PromptResult(PromptState.Failed, "The local AI answer was too large.");
            }

            var body = await ReadBoundedAsync(response, token);
            if (body is null)
            {
                return new PromptResult(PromptState.Failed, "The local AI answer was too large.");
            }

            var answer = ExtractLocalAnswer(body);
            return new PromptResult(
                PromptState.Succeeded,
                answer.Length == 0 ? "No answer." : answer);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return new PromptResult(PromptState.Cancelled, "Cancelled.");
        }
        catch (TaskCanceledException)
        {
            return new PromptResult(PromptState.Failed, "Local AI took too long to answer.");
        }
        catch (HttpRequestException)
        {
            return new PromptResult(
                PromptState.Failed,
                "Local AI is unreachable. Check that it is running, then try again.");
        }
        catch (Exception exception)
        {
            DiagnosticsService.Write("Prompt", exception);
            return new PromptResult(PromptState.Failed, "The prompt failed.");
        }
    }

    private static async Task<HttpResponseMessage?> SendLocalAsync(
        Uri endpoint,
        string payload,
        CancellationToken token)
    {
        var current = endpoint;

        for (var hop = 0; hop <= 3; hop++)
        {
            if (!SafeUrl.IsLoopbackOrPrivate(current.ToString(), out var target)
                || target is null
                || !await ResolvesToPrivateAsync(target, token))
            {
                return null;
            }

            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var request = new HttpRequestMessage(HttpMethod.Post, target) { Content = content };
            var response = await Http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                token);

            var status = (int)response.StatusCode;
            if (status is not (301 or 302 or 303 or 307 or 308))
            {
                return response;
            }

            var location = response.Headers.Location;
            response.Dispose();

            if (location is null || status is not (307 or 308))
            {
                return null;
            }

            current = location.IsAbsoluteUri ? location : new Uri(target, location);
        }

        return null;
    }

    private static async Task<bool> ResolvesToPrivateAsync(Uri uri, CancellationToken token)
    {
        if (uri.IsLoopback)
        {
            return true;
        }

        if (uri.HostNameType is not UriHostNameType.Dns)
        {
            return SafeUrl.IsLoopbackOrPrivate(uri.ToString(), out _);
        }

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(uri.DnsSafeHost, token);
            return addresses.Length > 0 && addresses.All(IsPrivateAddress);
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static bool IsPrivateAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal)
        {
            return true;
        }

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return (bytes[0] & 0xFE) == 0xFC; // fc00::/7 unique-local
        }

        return bytes[0] == 10
            || (bytes[0] == 192 && bytes[1] == 168)
            || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
            || (bytes[0] == 169 && bytes[1] == 254);
    }

    private static async Task<string?> ReadBoundedAsync(HttpResponseMessage response, CancellationToken token)
    {
        using var stream = await response.Content.ReadAsStreamAsync(token);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        var total = 0;

        int read;
        while ((read = await stream.ReadAsync(chunk, token)) > 0)
        {
            total += read;
            if (total > MaxResponseBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static string ExtractLocalAnswer(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("response", out var value)
                && value.ValueKind == JsonValueKind.String)
            {
                return Clamp(value.GetString()?.Trim() ?? string.Empty);
            }
        }
        catch (JsonException)
        {
        }

        return Clamp(body.Trim());
    }

    private static string ExtractGroqAnswer(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("choices", out var choices)
                || choices.ValueKind != JsonValueKind.Array
                || choices.GetArrayLength() == 0)
            {
                return string.Empty;
            }

            var first = choices[0];
            if (first.ValueKind != JsonValueKind.Object
                || !first.TryGetProperty("message", out var message)
                || message.ValueKind != JsonValueKind.Object
                || !message.TryGetProperty("content", out var content)
                || content.ValueKind != JsonValueKind.String)
            {
                return string.Empty;
            }

            return Clamp(content.GetString()?.Trim() ?? string.Empty);
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    private static string Clamp(string text)
        => text.Length <= MaxAnswerChars ? text : text[..MaxAnswerChars];

    public static bool OpenPhoneLink()
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", "ms-phone:")
            {
                UseShellExecute = true
            });
            return true;
        }
        catch
        {
            return false;
        }
    }
}
