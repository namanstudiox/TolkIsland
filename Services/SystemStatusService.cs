using System;
using System.Windows.Media;
using TolkIsland.Native;

namespace TolkIsland.Services;

/// <summary>Real system power information from GetSystemPowerStatus.</summary>
public static class SystemStatusService
{
    public const double UnknownPercent = 255; // System reports 255 when there is no battery.

    public static (bool HasBattery, bool OnAcPower, double Percent, bool IsCharging) GetPowerStatus()
    {
        if (!NativeMethods.GetSystemPowerStatus(out var status))
        {
            return (false, false, 0, false);
        }

        var onAc = status.ACLineStatus == 1;
        var percent = status.BatteryLifePercent;

        // Bit 7 means "no system battery"; 255% is the other no-battery signal.
        var noBattery = (status.BatteryFlag & 0x80) != 0;
        if (noBattery || percent > 100)
        {
            return (false, onAc, 0, false);
        }

        // BatteryFlag bit 3 means the battery is actually charging. 0xFF means
        // the flag is unknown, which must not be mistaken for "charging".
        var isCharging = status.BatteryFlag != 0xFF && (status.BatteryFlag & 0x08) != 0;

        return (true, onAc, percent, isCharging);
    }

    public static string FormatClock(DateTime time) => time.ToString("HH:mm");

    private static readonly string[] Accents =
    {
        "#FF3B82F6", "#FF8B5CF6", "#FFEC4899", "#FFF97316",
        "#FF22C55E", "#FF14B8A6", "#FF6366F1", "#FF0EA5E9"
    };

    /// <summary>Deterministic accent colour for a notification avatar, derived from the app name.</summary>
    public static SolidColorBrush AccentFor(string seed)
    {
        uint hash = 2166136261;
        foreach (var c in seed ?? string.Empty)
        {
            hash = (hash ^ c) * 16777619;
        }

        var color = Accents[hash % Accents.Length];
        return new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color));
    }


    public static string FormatDuration(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }

        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
            : $"{span.Minutes}:{span.Seconds:00}";
    }
}
