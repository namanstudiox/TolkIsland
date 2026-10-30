using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace TolkIsland.Services;

public sealed record UpdateInfo(string Version, string DownloadUrl, string Notes);

public enum UpdateCheckStatus
{
    UpToDate,
    UpdateAvailable,
    NotConfigured,
    Failed
}

public sealed record UpdateCheckResult(UpdateCheckStatus Status, UpdateInfo? Update, string Message)
{
    public static UpdateCheckResult UpToDate { get; } =
        new(UpdateCheckStatus.UpToDate, null, string.Empty);

    public static UpdateCheckResult NotConfigured(string message) =>
        new(UpdateCheckStatus.NotConfigured, null, message);

    public static UpdateCheckResult Failed(string message) =>
        new(UpdateCheckStatus.Failed, null, message);
}

public sealed class UpdateService
{
    private const int MaxManifestBytes = 8192;

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(12)
    };

    public static UpdateInfo? Available { get; private set; }

    public static Version CurrentVersion =>
        Version.TryParse(AppInfo.Version, out var parsed)
            ? parsed
            : typeof(UpdateService).Assembly.GetName().Version ?? new Version(1, 0, 0, 0);

    public static async Task<UpdateCheckResult> CheckAsync()
    {
        var settings = SettingsService.Current;
        if (!settings.UpdateCheckEnabled || string.IsNullOrWhiteSpace(settings.UpdateManifestUrl))
        {
            Available = null;
            return UpdateCheckResult.NotConfigured(
                "Add an update manifest URL in Settings → Advanced first.");
        }

        if (!SafeUrl.IsHttp(settings.UpdateManifestUrl, out var uri) || uri is null)
        {
            Available = null;
            return UpdateCheckResult.Failed("The update manifest address is not a valid web address.");
        }

        try
        {
            using var response = await Http.GetAsync(uri);
            if (!response.IsSuccessStatusCode)
            {
                Available = null;
                return UpdateCheckResult.Failed(
                    $"The update manifest returned HTTP {(int)response.StatusCode}.");
            }

            if (response.Content.Headers.ContentLength > MaxManifestBytes)
            {
                Available = null;
                return UpdateCheckResult.Failed("The update manifest is larger than 8 KB.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var buffer = new MemoryStream();
            var chunk = new byte[4096];
            int read;
            while ((read = await stream.ReadAsync(chunk)) > 0)
            {
                if (buffer.Length + read > MaxManifestBytes)
                {
                    Available = null;
                    return UpdateCheckResult.Failed("The update manifest is larger than 8 KB.");
                }

                buffer.Write(chunk, 0, read);
            }

            var body = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length)
                .TrimStart('\uFEFF');
            if (body.Length == 0)
            {
                Available = null;
                return UpdateCheckResult.Failed("The update manifest is empty.");
            }

            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (!root.TryGetProperty("version", out var versionElement)
                || !Version.TryParse(versionElement.GetString(), out var version))
            {
                Available = null;
                return UpdateCheckResult.Failed("The update manifest has no valid version field.");
            }

            var url = root.TryGetProperty("url", out var urlElement) ? urlElement.GetString() : null;
            var notes = root.TryGetProperty("notes", out var notesElement) ? notesElement.GetString() : null;

            if (!SafeUrl.IsHttp(url, out _))
            {
                Available = null;
                return UpdateCheckResult.Failed(
                    "The update manifest has no valid download link. Nothing can be opened.");
            }

            if (version <= CurrentVersion)
            {
                Available = null;
                return UpdateCheckResult.UpToDate;
            }

            Available = new UpdateInfo(
                version.ToString(),
                url!,
                notes?.Trim() ?? string.Empty);

            return new UpdateCheckResult(
                UpdateCheckStatus.UpdateAvailable,
                Available,
                string.Empty);
        }
        catch (TaskCanceledException)
        {
            Available = null;
            return UpdateCheckResult.Failed("The update check timed out.");
        }
        catch (JsonException)
        {
            Available = null;
            return UpdateCheckResult.Failed("The update manifest is not valid JSON.");
        }
        catch (HttpRequestException)
        {
            Available = null;
            return UpdateCheckResult.Failed("The update manifest could not be reached.");
        }
        catch (Exception exception)
        {
            DiagnosticsService.Write("UpdateCheck", exception);
            Available = null;
            return UpdateCheckResult.Failed("The update check failed. See the local log for details.");
        }
    }

    public static bool OpenDownloadPage(UpdateInfo info)
    {
        if (!SafeUrl.IsHttp(info.DownloadUrl, out var uri) || uri is null)
        {
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
            return true;
        }
        catch
        {
            return false;
        }
    }
}
