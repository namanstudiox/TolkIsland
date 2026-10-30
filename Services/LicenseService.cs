using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace TolkIsland.Services;

/// <summary>
/// Local Pro unlock. There is no licence server in this repository; a
/// well-formed Tolk Island key is enough to mark this install as Pro.
/// Replace this with a signed store receipt when you ship.
/// </summary>
public static class LicenseService
{
    private static readonly Regex KeyPattern = new(
        @"^TOLK-[A-Z0-9]{4}-[A-Z0-9]{4}-[A-Z0-9]{4}$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool IsPro =>
        SettingsService.Current.ProUnlocked || IsValid(SettingsService.Current.LicenseKey);

    public static bool TryActivate(string? key)
    {
        var trimmed = key?.Trim().ToUpperInvariant() ?? string.Empty;
        if (!IsValid(trimmed))
        {
            return false;
        }

        SettingsService.Update(settings =>
        {
            settings.LicenseKey = trimmed;
            settings.ProUnlocked = true;
        });

        return true;
    }

    public static void Deactivate()
        => SettingsService.Update(settings =>
        {
            settings.LicenseKey = string.Empty;
            settings.ProUnlocked = false;
        });

    public static bool IsValid(string? key)
    {
        if (string.IsNullOrWhiteSpace(key) || !KeyPattern.IsMatch(key))
        {
            return false;
        }

        var compact = key.Replace("-", "", StringComparison.Ordinal).ToUpperInvariant();
        var body = compact[4..];
        var payload = body[..8];
        var checksum = body[8..];
        return string.Equals(checksum, Checksum(payload), StringComparison.Ordinal);
    }

    /// <summary>Builds a valid local key for this machine. Not a store receipt.</summary>
    public static string GenerateLocalKey()
    {
        var seed = Environment.MachineName + "|" + Environment.UserName;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        var payload = ToAlphabet(hash, 8);
        return $"TOLK-{payload[..4]}-{payload[4..8]}-{Checksum(payload)}";
    }

    private static string Checksum(string payload)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes("TolkIsland|" + payload));
        return ToAlphabet(bytes, 4);
    }

    private static string ToAlphabet(byte[] hash, int length)
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var chars = new char[length];
        for (var i = 0; i < length; i++)
        {
            chars[i] = alphabet[hash[i] % alphabet.Length];
        }

        return new string(chars);
    }
}
