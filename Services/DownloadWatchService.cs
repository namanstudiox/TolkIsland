using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Timer = System.Threading.Timer;

namespace TolkIsland.Services;

public sealed record DownloadInfo(string Path, string Name, long Bytes);

/// <summary>A download that is still growing on disk (browser .crdownload etc.).</summary>
public sealed record TransferInfo(string Name, long Bytes, double BytesPerSecond);

/// <summary>
/// Watches the user Downloads folder for new files. Temporary browser
/// placeholders (.crdownload, .tmp, .partial) are ignored until they settle,
/// but while one is growing the island can show a live transfer activity.
/// </summary>
public sealed class DownloadWatchService : IDisposable
{
    private static readonly string[] IgnoreExtensions =
        { ".crdownload", ".tmp", ".part", ".partial", ".download" };

    private readonly object _sync = new();
    private readonly Dictionary<string, (long Size, DateTime Stamp, DateTime Started)> _transfers = new();
    private FileSystemWatcher? _watcher;
    private Timer? _settle;
    private Timer? _progress;
    private string? _pending;
    private volatile bool _disposed;

    public event Action<DownloadInfo>? FileArrived;

    public event Action<TransferInfo>? TransferProgress;

    public void Start()
    {
        if (_disposed || !SettingsService.Current.DownloadsEnabled)
        {
            return;
        }

        lock (_sync)
        {
            if (_watcher is not null)
            {
                return;
            }

            var folder = GetDownloadsFolder();
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            {
                return;
            }

            try
            {
                _watcher = new FileSystemWatcher(folder)
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    IncludeSubdirectories = false,
                    EnableRaisingEvents = true
                };
                _watcher.Created += (_, args) => OnCreated(args.FullPath);
                _watcher.Renamed += (_, args) => OnRenamed(args.OldFullPath, args.FullPath);
                _watcher.Deleted += (_, args) => Forget(args.FullPath);
                _settle = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
                // Started on demand by TrackTransfer; an idle watcher costs nothing.
                _progress = new Timer(_ => PollTransfers(), null, Timeout.Infinite, Timeout.Infinite);
            }
            catch
            {
                _watcher?.Dispose();
                _watcher = null;
            }
        }
    }

    public void ApplySettings(bool enabled)
    {
        if (enabled)
        {
            Start();
            return;
        }

        Stop();
    }

    public void Stop()
    {
        lock (_sync)
        {
            _watcher?.Dispose();
            _watcher = null;
            _settle?.Dispose();
            _settle = null;
            _progress?.Dispose();
            _progress = null;
            _transfers.Clear();
            _pending = null;
        }
    }

    private static bool IsTemporary(string path)
        => IgnoreExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private void OnCreated(string path)
    {
        if (IsTemporary(path))
        {
            TrackTransfer(path);
            return;
        }

        Queue(path);
    }

    private void OnRenamed(string oldPath, string newPath)
    {
        Forget(oldPath);

        if (IsTemporary(newPath))
        {
            TrackTransfer(newPath);
            return;
        }

        Queue(newPath);
    }

    private void TrackTransfer(string path)
    {
        if (_disposed)
        {
            return;
        }

        lock (_sync)
        {
            _transfers[path] = (0, DateTime.UtcNow, DateTime.UtcNow);
        }

        _progress?.Change(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    private void Forget(string path)
    {
        lock (_sync)
        {
            _transfers.Remove(path);
        }
    }

    /// <summary>
    /// Compares each temporary file with its previous size once a second. The
    /// growth is the real transfer rate; files that stop growing (paused
    /// downloads) simply stop reporting.
    /// </summary>
    private void PollTransfers()
    {
        if (_disposed)
        {
            return;
        }

        List<(string Path, TransferInfo Info)> updates = new();

        lock (_sync)
        {
            if (_transfers.Count == 0)
            {
                _progress?.Change(Timeout.Infinite, Timeout.Infinite);
                return;
            }

            foreach (var path in _transfers.Keys.ToList())
            {
                try
                {
                    if (!File.Exists(path))
                    {
                        _transfers.Remove(path);
                        continue;
                    }

                    var size = new FileInfo(path).Length;
                    var (previous, stamp, started) = _transfers[path];
                    var seconds = Math.Max(0.2, (DateTime.UtcNow - stamp).TotalSeconds);

                    _transfers[path] = (size, DateTime.UtcNow, started);

                    if (size <= previous)
                    {
                        // A paused or stalled download stops reporting, and after
                        // half an hour the entry is dropped so the map cannot grow
                        // for files the browser abandoned.
                        if (DateTime.UtcNow - started > TimeSpan.FromMinutes(30))
                        {
                            _transfers.Remove(path);
                        }

                        continue;
                    }

                    var name = Path.GetFileNameWithoutExtension(path);
                    updates.Add((path, new TransferInfo(
                        name,
                        size,
                        (size - previous) / seconds)));
                }
                catch
                {
                    // The browser may still be holding the file open.
                }
            }
        }

        foreach (var update in updates)
        {
            TransferProgress?.Invoke(update.Info);
        }
    }

    private void Queue(string path)
    {
        if (_disposed || string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (IsTemporary(path))
        {
            return;
        }

        lock (_sync)
        {
            _pending = path;
            _settle?.Change(TimeSpan.FromMilliseconds(800), Timeout.InfiniteTimeSpan);
        }
    }


    private void Flush()
    {
        string? path;
        lock (_sync)
        {
            path = _pending;
            _pending = null;
        }

        if (path is null || !File.Exists(path))
        {
            return;
        }

        try
        {
            var info = new FileInfo(path);
            if (info.Length <= 0)
            {
                return;
            }

            FileArrived?.Invoke(new DownloadInfo(path, info.Name, info.Length));
        }
        catch
        {
            // File still locked by the browser.
        }
    }

    private static string GetDownloadsFolder()
    {
        try
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads");
        }
        catch
        {
            return string.Empty;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _settle?.Dispose();
        _progress?.Dispose();
        _watcher?.Dispose();
    }
}
