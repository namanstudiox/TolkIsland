using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TolkIsland.Services;

/// <summary>
/// File I/O for settings, separated from the live singleton so the same code
/// path can be exercised against a temporary directory in tests.
/// </summary>
public static class SettingsStorage
{
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Writes a temp file then swaps it in, so a crash cannot truncate the target.</summary>
    public static SaveResult WriteAtomic(string path, string json)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temp = path + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, path, overwrite: true);
            return SaveResult.Success;
        }
        catch (Exception exception)
        {
            return SaveResult.From(exception);
        }
    }

    public static SaveResult Save(string path, AppSettings settings)
    {
        try
        {
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            BackupExisting(path);
            return WriteAtomic(path, json);
        }
        catch (Exception exception)
        {
            return SaveResult.From(exception);
        }
    }

    /// <summary>
    /// A corrupt file is moved aside and the last good backup is used instead of
    /// silently overwriting every preference with defaults.
    /// </summary>
    public static AppSettings Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new AppSettings();
            }

            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        }
        catch
        {
            return Recover(path);
        }
    }

    private static void BackupExisting(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return;
            }

            var existing = File.ReadAllText(path);
            _ = JsonSerializer.Deserialize<AppSettings>(existing, JsonOptions)
                ?? throw new JsonException("Settings backup read an empty file.");
            File.Copy(path, path + ".bak", overwrite: true);
        }
        catch
        {
            // A backup is best-effort; the atomic write still protects the target.
        }
    }

    private static AppSettings Recover(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Copy(path, path + ".bad", overwrite: true);
                DiagnosticsService.WriteLine("settings", "settings.json was unreadable; saved as settings.json.bad");
            }
        }
        catch
        {
            // Quarantine is best-effort; recovery still tries the backup.
        }

        try
        {
            var backup = path + ".bak";
            if (File.Exists(backup))
            {
                var json = File.ReadAllText(backup);
                var restored = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (restored is not null)
                {
                    DiagnosticsService.WriteLine("settings", "Recovered settings from settings.json.bak");
                    return restored;
                }
            }
        }
        catch
        {
            // A bad backup also falls through to defaults.
        }

        DiagnosticsService.WriteLine("settings", "No readable settings; starting from defaults");
        return new AppSettings();
    }
}
