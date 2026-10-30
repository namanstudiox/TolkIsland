using System;
using System.IO;
using System.Reflection;
using Microsoft.Win32;

namespace TolkIsland.Services;

public static class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "TolkIsland";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            var saved = key?.GetValue(ValueName) as string;
            var expected = BuildCommand();
            return expected is not null
                && string.Equals(saved, expected, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static bool SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                return false;
            }

            if (enabled)
            {
                var command = BuildCommand();
                if (command is null || command.Length >= 260)
                {
                    return false;
                }

                key.SetValue(ValueName, command, RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? BuildCommand()
    {
        var process = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(process)
            || !Path.IsPathFullyQualified(process)
            || !File.Exists(process))
        {
            return null;
        }

        var entry = Assembly.GetEntryAssembly();
        if (entry is not null
            && string.Equals(Path.GetFileName(process), "dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            var managed = Path.Combine(
                AppContext.BaseDirectory,
                (entry.GetName().Name ?? "TolkIsland") + ".dll");

            return File.Exists(managed)
                ? $"\"{process}\" \"{managed}\""
                : null;
        }

        return $"\"{process}\"";
    }
}
