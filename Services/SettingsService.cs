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

    public bool MirrorEnabled { get; set; }

    public bool UpdateCheckEnabled { get; set; }

    public string UpdateManifestUrl { get; set; } = string.Empty;

    public bool NotificationReplyEnabled { get; set; } = true;

    public bool SwipeNavigationEnabled { get; set; } = true;

    public int NotificationVisibleSeconds { get; set; } = 4;

    public int IdleHideSeconds { get; set; } = 10;

    public bool AnalyticsOptIn { get; set; }

    public bool AutoUpdateCheck { get; set; }

    public bool ProUnlocked { get; set; }

    public string LicenseKey { get; set; } = string.Empty;

    public List<string> QuickLaunch { get; set; } = new();

    [JsonIgnore]
    public string ThemeLabel => Theme switch
    {
        ThemeMode.Light => "Light",
        ThemeMode.System => "System",
        _ => "Dark"
    };
}

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

    private static AppSettings _current = Load();

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
