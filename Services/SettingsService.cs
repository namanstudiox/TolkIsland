using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TolkIsland.Services;

public enum MonitorMode
{
    Primary,
    ActiveWindow,
    Specific,
    All
}

public enum ExpandMode
{
    Hover,
    Click
}

public enum PositionMode
{
    TopCenter,
    TopOffset,
    FloatingPill
}

public enum ThemeMode
{
    Dark,
    Light,
    System
}

public enum SkinMode
{
    Classic,
    Spider
}

public sealed class AppSettings
{
    public int Version { get; set; } = 1;

    public MonitorMode MonitorMode { get; set; } = MonitorMode.Primary;

    public string? MonitorDeviceName { get; set; }

    public PositionMode PositionMode { get; set; } = PositionMode.TopCenter;

    public double HorizontalOffset { get; set; }

    public double TopOffset { get; set; }

    public double Scale { get; set; } = 1.0;

    public double CornerRadius { get; set; } = 30;

    public ThemeMode Theme { get; set; } = ThemeMode.Dark;

    public SkinMode Skin { get; set; } = SkinMode.Classic;

    public string AccentColor { get; set; } = "#FF0A84FF";

    public bool ReducedMotion { get; set; }

    public bool HighContrast { get; set; }

    public bool IdleHide { get; set; } = true;

    public string Hotkey { get; set; } = "Ctrl+Alt+I";

    public bool MediaEnabled { get; set; } = true;

    public bool FocusEnabled { get; set; } = true;

    public bool NotificationsEnabled { get; set; } = true;

    public bool ChargingEnabled { get; set; } = true;

    public bool VolumeEnabled { get; set; } = true;

    public bool BrightnessEnabled { get; set; } = true;

    public bool ShelfEnabled { get; set; } = true;

    public bool QuietMode { get; set; }

    public bool MediaFirst { get; set; }

    public List<string> BlockedApps { get; set; } = new();

    public List<string> PriorityApps { get; set; } = new();

    public List<string> ShelfFiles { get; set; } = new();

    public bool OnboardingCompleted { get; set; }

    public ExpandMode ExpandMode { get; set; } = ExpandMode.Hover;

    public bool NotificationSounds { get; set; }

    public bool HapticsEnabled { get; set; }

    public bool CalendarEnabled { get; set; } = true;

    public bool DownloadsEnabled { get; set; } = true;

    public bool PromptEnabled { get; set; }

    /// <summary>Front-camera preview. Off by default; no frames are recorded.</summary>
    public bool MirrorEnabled { get; set; }

    /// <summary>Opt-in update check against a JSON manifest the vendor hosts.</summary>
    public bool UpdateCheckEnabled { get; set; }

    public string UpdateManifestUrl { get; set; } = string.Empty;

    /// <summary>Shows the Reply entry for message-style toasts.</summary>
    public bool NotificationReplyEnabled { get; set; } = true;

    /// <summary>Shift + wheel (and horizontal drag) cycles through activities.</summary>
    public bool SwipeNavigationEnabled { get; set; } = true;

    /// <summary>How long a notification peek stays open, in seconds.</summary>
    public int NotificationVisibleSeconds { get; set; } = 4;

    /// <summary>How long the idle pill waits before it retracts to the thin tab.</summary>
    public int IdleHideSeconds { get; set; } = 10;

    public bool AnalyticsOptIn { get; set; }

    public bool AutoUpdateCheck { get; set; }

    public bool ProUnlocked { get; set; }

    public string LicenseKey { get; set; } = string.Empty;

    public string LocalAiEndpoint { get; set; } = string.Empty;

    public string LocalAiModel { get; set; } = "llama3.2";

    public const string DefaultGroqModel = "llama-3.3-70b-versatile";

    /// <summary>Runs the Ask panel on Groq's hosted API instead of the local endpoint.</summary>
    public bool GroqEnabled { get; set; }

    /// <summary>DPAPI-protected Groq key. The plaintext is never written to disk.</summary>
    public string GroqApiKeyProtected { get; set; } = string.Empty;

    public string GroqModel { get; set; } = DefaultGroqModel;

    public List<string> QuickLaunch { get; set; } = new();

    [JsonIgnore]
    public string ThemeLabel => Theme switch
    {
        ThemeMode.Light => "Light",
        ThemeMode.System => "System",
        _ => "Dark"
    };
}

/// <summary>
/// The presentation-only slice of the settings. Sent while the Settings window
/// is open so the real island can follow sliders without saving anything yet.
/// </summary>
public sealed record IslandPreview(
    PositionMode PositionMode,
    double HorizontalOffset,
    double TopOffset,
    double Scale,
    double CornerRadius,
    ThemeMode Theme,
    string AccentColor,
    SkinMode Skin,
    bool HighContrast,
    bool ReducedMotion);

/// <summary>
/// One small, local JSON file for every user choice. Settings are deliberately
/// stored under LocalAppData (not the install directory) so the app can live in
/// Program Files or a portable folder without write-access problems.
/// </summary>
public static class SettingsService
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = SettingsStorage.JsonOptions;

    private static readonly string DefaultDataDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TolkIsland");

    private static string? _dataDirectoryOverride;

    public static string DataDirectory => _dataDirectoryOverride ?? DefaultDataDirectory;

    public static string SettingsPath => Path.Combine(DataDirectory, "settings.json");

    // Must follow DataDirectory: Load() reads through SettingsPath, which uses it.
    private static AppSettings _current = Load();

    /// <summary>
    /// Points every settings read and write at another folder. The regression
    /// harness uses this so a run can never touch the signed-in user's file.
    /// </summary>
    public static void OverrideDataDirectory(string? directory)
    {
        lock (Gate)
        {
            _dataDirectoryOverride = string.IsNullOrWhiteSpace(directory) ? null : directory;
            _current = Load();
        }
    }

    public static AppSettings Current
    {
        get
        {
            lock (Gate)
            {
                return _current;
            }
        }
    }

    public static event Action? Changed;

    public static event Action<IslandPreview>? Previewed;

    public static event Action? PreviewEnded;

    public static void Preview(IslandPreview preview) => Previewed?.Invoke(preview);

    public static void EndPreview() => PreviewEnded?.Invoke();

    public static SaveResult Update(Action<AppSettings> mutate)
    {
        if (mutate is null)
        {
            return SaveResult.Fail(SaveFailure.Unknown, "No settings change was provided.");
        }

        SaveResult result;
        lock (Gate)
        {
            // Mutate a clone and swap it in only after the file is written. A
            // failed save must leave the running app and the disk in agreement
            // instead of showing settings that will vanish on restart.
            var next = Clone(_current);
            mutate(next);
            Normalize(next);
            result = SaveLocked(next);
            if (result.Ok)
            {
                _current = next;
            }
        }

        if (result.Ok)
        {
            Changed?.Invoke();
        }

        return result;
    }

    public static SaveResult Save()
    {
        SaveResult result;
        lock (Gate)
        {
            Normalize(_current);
            result = SaveLocked(_current);
        }

        return result;
    }

    public static SaveResult Reset()
    {
        SaveResult result;
        lock (Gate)
        {
            var defaults = new AppSettings();
            Normalize(defaults);
            result = SaveLocked(defaults);
            if (result.Ok)
            {
                _current = defaults;
            }
        }

        if (result.Ok)
        {
            Changed?.Invoke();
        }

        return result;
    }

    private static AppSettings Clone(AppSettings settings)
    {
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
    }

    private static AppSettings Load()
    {
        // A corrupt settings file must not stop the island from starting.
        var loaded = SettingsStorage.Load(SettingsPath);
        Normalize(loaded);
        return loaded;
    }

    private static void Normalize(AppSettings settings)
    {
        settings.Scale = Math.Clamp(settings.Scale <= 0 ? 1 : settings.Scale, 0.85, 1.25);
        settings.CornerRadius = Math.Clamp(settings.CornerRadius, 0, 40);
        settings.HorizontalOffset = Math.Abs(settings.HorizontalOffset) < 0.5
            ? 0
            : Math.Clamp(settings.HorizontalOffset, -2400, 2400);
        settings.TopOffset = Math.Abs(settings.TopOffset) < 0.5
            ? 0
            : Math.Clamp(settings.TopOffset, 0, 400);
        settings.BlockedApps ??= new List<string>();
        settings.PriorityApps ??= new List<string>();
        settings.ShelfFiles ??= new List<string>();
        settings.QuickLaunch ??= new List<string>();
        settings.LicenseKey ??= string.Empty;
        settings.LocalAiEndpoint ??= string.Empty;
        settings.LocalAiModel ??= "llama3.2";
        settings.GroqApiKeyProtected ??= string.Empty;
        settings.GroqModel = string.IsNullOrWhiteSpace(settings.GroqModel)
            ? AppSettings.DefaultGroqModel
            : settings.GroqModel.Trim();
        if (settings.GroqModel.Length > 80)
        {
            settings.GroqModel = settings.GroqModel[..80];
        }

        if (settings.GroqApiKeyProtected.Length > 4096)
        {
            settings.GroqApiKeyProtected = settings.GroqApiKeyProtected[..4096];
        }

        settings.UpdateManifestUrl ??= string.Empty;

        settings.BlockedApps = CleanList(settings.BlockedApps);
        settings.PriorityApps = CleanList(settings.PriorityApps);
        settings.ShelfFiles = settings.ShelfFiles
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToList();

        settings.QuickLaunch = settings.QuickLaunch
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();

        if (string.IsNullOrWhiteSpace(settings.Hotkey))
        {
            settings.Hotkey = "Ctrl+Alt+I";
        }

        if (!Enum.IsDefined(settings.Skin))
        {
            settings.Skin = SkinMode.Classic;
        }

        settings.NotificationVisibleSeconds = Math.Clamp(settings.NotificationVisibleSeconds, 2, 30);
        settings.IdleHideSeconds = Math.Clamp(settings.IdleHideSeconds, 3, 120);

        if (!IsColor(settings.AccentColor))
        {
            settings.AccentColor = "#FF0A84FF";
        }
    }

    private static List<string> CleanList(IEnumerable<string> values)
        => values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(40)
            .ToList();

    private static bool IsColor(string value)
    {
        try
        {
            _ = System.Windows.Media.ColorConverter.ConvertFromString(value);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static SaveResult SaveLocked(AppSettings settings) => SettingsStorage.Save(SettingsPath, settings);
}
