using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace TolkIsland.Services;

public enum SaveFailure
{
    None,
    AccessDenied,
    DiskFull,
    Serialization,
    Unknown
}

public readonly record struct SaveResult(bool Ok, SaveFailure Failure, string Message)
{
    public static SaveResult Success { get; } = new(true, SaveFailure.None, string.Empty);

    public static SaveResult Fail(SaveFailure failure, string message) => new(false, failure, message);

    public static SaveResult From(Exception exception) => exception switch
    {
        UnauthorizedAccessException => Fail(
            SaveFailure.AccessDenied,
            "Windows denied write access. Check the folder permissions and try again."),
        IOException io when IsDiskFull(io) => Fail(
            SaveFailure.DiskFull,
            "The disk is full. Free some space and try again."),
        System.Text.Json.JsonException => Fail(
            SaveFailure.Serialization,
            "The data could not be serialized."),
        _ => Fail(SaveFailure.Unknown, "The file could not be written.")
    };

    private static bool IsDiskFull(IOException exception)
    {
        var code = exception.HResult & 0xFFFF;
        return code is 39 or 112;
    }
}

public static class AppInfo
{
    public const int ApiVersion = 2;

    public static string Version
    {
        get
        {
            var assembly = typeof(AppInfo).Assembly;
            var informational = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion;

            if (!string.IsNullOrWhiteSpace(informational))
            {
                var plus = informational.IndexOf('+');
                return plus >= 0 ? informational[..plus] : informational;
            }

            return (assembly.GetName().Version ?? new Version(1, 0, 0, 0)).ToString(3);
        }
    }
}

public enum CommandDisposition
{
    Accepted,
    IgnoredPaused,
    Disabled,
    Unknown
}

public static class IslandCommandPolicy
{
    public static CommandDisposition Evaluate(bool paused, bool featureEnabled, bool knownCommand)
    {
        if (!knownCommand)
        {
            return CommandDisposition.Unknown;
        }

        if (paused)
        {
            return CommandDisposition.IgnoredPaused;
        }

        return featureEnabled ? CommandDisposition.Accepted : CommandDisposition.Disabled;
    }
}

public sealed class NotificationQueue<T>
{
    private readonly object _sync = new();
    private readonly Queue<T> _items = new();

    public NotificationQueue(int capacity)
    {
        Capacity = Math.Max(1, capacity);
    }

    public int Capacity { get; }

    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _items.Count;
            }
        }
    }

    public bool TryEnqueue(T item)
    {
        lock (_sync)
        {
            var keptAll = _items.Count < Capacity;
            if (!keptAll)
            {
                _items.Dequeue();
            }

            _items.Enqueue(item);
            return keptAll;
        }
    }

    public bool TryDequeue(out T item)
    {
        lock (_sync)
        {
            if (_items.Count == 0)
            {
                item = default!;
                return false;
            }

            item = _items.Dequeue();
            return true;
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _items.Clear();
        }
    }
}

public sealed class CaptureLease
{
    private readonly object _sync = new();
    private int _count;
    private long _generation;

    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _count;
            }
        }
    }

    public long Generation
    {
        get
        {
            lock (_sync)
            {
                return _generation;
            }
        }
    }

    public bool Acquire()
    {
        lock (_sync)
        {
            _count++;
            return _count == 1;
        }
    }

    public bool Release()
    {
        lock (_sync)
        {
            if (_count == 0)
            {
                return false;
            }

            _count--;
            if (_count > 0)
            {
                return false;
            }

            _generation++;
            return true;
        }
    }

    public long StartSession()
    {
        lock (_sync)
        {
            return ++_generation;
        }
    }

    public bool IsCurrent(long generation)
    {
        lock (_sync)
        {
            return generation == _generation;
        }
    }
}

public static class SafeUrl
{
    private static readonly string[] MeetingHosts =
    {
        "teams.microsoft.com",
        "zoom.us",
        "meet.google.com",
        "webex.com"
    };

    public static bool IsHttp(string? value, out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(value)
            || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var parsed))
        {
            return false;
        }

        if (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(parsed.Host) || parsed.UserInfo.Length > 0)
        {
            return false;
        }

        uri = parsed;
        return true;
    }

    public static bool IsTrustedMeeting(string? value, out Uri? uri)
    {
        uri = null;
        if (!IsHttp(value, out var parsed) || parsed is null)
        {
            return false;
        }

        var host = parsed.Host.TrimEnd('.').ToLowerInvariant();
        var trusted = MeetingHosts.Any(known =>
            host == known || host.EndsWith("." + known, StringComparison.Ordinal));
        if (!trusted)
        {
            return false;
        }

        uri = parsed;
        return true;
    }

    public static bool IsLoopbackOrPrivate(string? value, out Uri? uri)
    {
        uri = null;
        if (!IsHttp(value, out var parsed) || parsed is null)
        {
            return false;
        }

        if (parsed.IsLoopback)
        {
            uri = parsed;
            return true;
        }

        if (parsed.HostNameType == UriHostNameType.IPv4)
        {
            var parts = parsed.Host.Split('.');
            if (parts.Length == 4
                && byte.TryParse(parts[0], out var a)
                && byte.TryParse(parts[1], out var b))
            {
                var privateRange = a == 10
                    || (a == 192 && b == 168)
                    || (a == 172 && b is >= 16 and <= 31);
                if (privateRange)
                {
                    uri = parsed;
                    return true;
                }
            }

            return false;
        }

        if (parsed.HostNameType == UriHostNameType.Dns
            && (parsed.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
                || parsed.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)))
        {
            uri = parsed;
            return true;
        }

        return false;
    }
}
