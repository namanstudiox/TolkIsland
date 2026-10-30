using System;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media;
using Windows.Media.Control;
using Windows.Storage.Streams;
using Timer = System.Threading.Timer;

namespace TolkIsland.Services;

/// <summary>Now Playing data read from the real Windows media sessions (SMTC).</summary>
public sealed record MediaInfo(
    string Title,
    string Artist,
    string SourceApp,
    bool HasMedia,
    bool IsPlaying,
    byte[]? Thumbnail);

public readonly record struct MediaTimeline(TimeSpan Position, TimeSpan Duration);

public sealed class MediaSessionService : IDisposable
{
    private const long MaxThumbnailBytes = 2 * 1024 * 1024;
    private static readonly TimeSpan ThumbnailRetryDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ThumbnailSettleWindow = TimeSpan.FromSeconds(6);

    // Some players raise MediaPropertiesChanged before they publish the new
    // metadata, so an immediate read still returns the previous track. Re-read
    // shortly after every change, and keep a slow poll as a safety net for
    // players that never raise that event at all.
    private static readonly TimeSpan MetadataRetryDelay = TimeSpan.FromMilliseconds(600);
    private static readonly TimeSpan MetadataPollInterval = TimeSpan.FromSeconds(2);

    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly Timer _metadataRetry;
    private readonly Timer _metadataPoll;

    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private MediaInfo? _current;
    private int _refreshVersion;
    private volatile bool _disposed;

    // Playback events fire often (pause, resume, timeline); the artwork itself
    // only changes when the track does, so it is streamed once per track. A
    // missing thumbnail is retried occasionally in case the app publishes it late.
    private sealed record ThumbnailCache(
        string Key,
        byte[]? Bytes,
        DateTime FirstSeenUtc,
        DateTime AttemptUtc);

    private ThumbnailCache _thumbnailCache = new(string.Empty, null, DateTime.MinValue, DateTime.MinValue);

    public MediaSessionService()
    {
        _metadataRetry = new Timer(_ => _ = RefreshAsync(), null, Timeout.Infinite, Timeout.Infinite);
        _metadataPoll = new Timer(_ => _ = RefreshAsync(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Last known session state, so a window created late can seed itself.</summary>
    public MediaInfo? Current => _current;

    public event Action<MediaInfo?>? CurrentChanged;
    public event Action<MediaTimeline>? TimelineChanged;

    public async Task StartAsync()
    {
        if (_disposed || _manager is not null)
        {
            return;
        }

        await _startGate.WaitAsync();
        try
        {
            if (_manager is not null)
            {
                return;
            }

            try
            {
                var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                if (manager is null)
                {
                    return;
                }

                _manager = manager;
                manager.CurrentSessionChanged += (_, _) => Attach(manager.GetCurrentSession());
                manager.SessionsChanged += (_, _) => Attach(manager.GetCurrentSession());

                Attach(manager.GetCurrentSession());
            }
            catch
            {
                // SMTC can be unavailable (or fail transiently). RefreshAsync retries.
                _manager = null;
            }
        }
        finally
        {
            _startGate.Release();
        }
    }

    private void Attach(GlobalSystemMediaTransportControlsSession? session)
    {
        if (_disposed)
        {
            return;
        }

        Interlocked.Increment(ref _refreshVersion);

        if (_session is not null)
        {
            _session.MediaPropertiesChanged -= OnProperties;
            _session.PlaybackInfoChanged -= OnPlayback;
            _session.TimelinePropertiesChanged -= OnTimeline;
        }

        _session = session;

        if (session is not null)
        {
            session.MediaPropertiesChanged += OnProperties;
            session.PlaybackInfoChanged += OnPlayback;
            session.TimelinePropertiesChanged += OnTimeline;
        }

        _metadataPoll.Change(
            session is null ? Timeout.InfiniteTimeSpan : MetadataPollInterval,
            session is null ? Timeout.InfiniteTimeSpan : MetadataPollInterval);

        _ = RefreshAsync();
    }

    private void OnProperties(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
    {
        _ = RefreshAsync();
        _metadataRetry.Change(MetadataRetryDelay, Timeout.InfiniteTimeSpan);
    }

    private void OnPlayback(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
    {
        _ = RefreshAsync();
        _metadataRetry.Change(MetadataRetryDelay, Timeout.InfiniteTimeSpan);
    }

    private void OnTimeline(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args)
        => RaiseTimeline(sender);

    public async Task RefreshAsync()
    {
        if (_disposed)
        {
            return;
        }

        if (_manager is null)
        {
            await StartAsync();
        }

        var session = _session;
        if (session is null)
        {
            if (_current is not null)
            {
                _current = null;
                CurrentChanged?.Invoke(null);
            }

            return;
        }

        var version = Interlocked.Increment(ref _refreshVersion);

        try
        {
            var props = await session.TryGetMediaPropertiesAsync();
            var playback = session.GetPlaybackInfo();
            var appId = session.SourceAppUserModelId ?? string.Empty;
            var thumbnail = await ResolveThumbnailAsync(props, appId);

            // A newer refresh (or a session switch) wins; drop stale results.
            if (version != Volatile.Read(ref _refreshVersion) || !ReferenceEquals(session, _session))
            {
                return;
            }

            var info = new MediaInfo(
                props.Title ?? string.Empty,
                props.Artist ?? string.Empty,
                PrettyAppName(appId),
                true,
                playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                thumbnail);

            if (!SameMedia(_current, info))
            {
                _current = info;
                CurrentChanged?.Invoke(info);
                RaiseTimeline(session);
            }
        }
        catch
        {
            // Transient WinRT errors are ignored; the next event or tick retries.
        }
    }

    public MediaTimeline? GetTimeline()
    {
        var session = _session;
        if (session is null)
        {
            return null;
        }

        try
        {
            return ReadTimeline(session);
        }
        catch
        {
            return null;
        }
    }

    public Task TogglePlayPauseAsync()
        => _session?.TryTogglePlayPauseAsync().AsTask() ?? Task.CompletedTask;

    public Task SkipNextAsync()
        => _session?.TrySkipNextAsync().AsTask() ?? Task.CompletedTask;

    public Task SkipPreviousAsync()
        => _session?.TrySkipPreviousAsync().AsTask() ?? Task.CompletedTask;

    public bool IsShuffleEnabled
    {
        get
        {
            try
            {
                return _session?.GetPlaybackInfo().IsShuffleActive == true;
            }
            catch
            {
                return false;
            }
        }
    }

    public MediaPlaybackAutoRepeatMode RepeatMode
    {
        get
        {
            try
            {
                return _session?.GetPlaybackInfo().AutoRepeatMode ?? MediaPlaybackAutoRepeatMode.None;
            }
            catch
            {
                return MediaPlaybackAutoRepeatMode.None;
            }
        }
    }

    public async Task ToggleShuffleAsync()
    {
        var session = _session;
        if (session is null)
        {
            return;
        }

        try
        {
            await session.TryChangeShuffleActiveAsync(!IsShuffleEnabled);
            await RefreshAsync();
        }
        catch
        {
            // The source app may not implement shuffle.
        }
    }

    public async Task CycleRepeatAsync()
    {
        var session = _session;
        if (session is null)
        {
            return;
        }

        var next = NextRepeatMode(RepeatMode);

        try
        {
            await session.TryChangeAutoRepeatModeAsync(next);
            await RefreshAsync();
        }
        catch
        {
            // The source app may not implement repeat.
        }
    }

    public static MediaPlaybackAutoRepeatMode NextRepeatMode(MediaPlaybackAutoRepeatMode mode) => mode switch
    {
        MediaPlaybackAutoRepeatMode.None => MediaPlaybackAutoRepeatMode.List,
        MediaPlaybackAutoRepeatMode.List => MediaPlaybackAutoRepeatMode.Track,
        _ => MediaPlaybackAutoRepeatMode.None
    };

    public Task SeekAsync(TimeSpan position)
    {
        var session = _session;
        if (session is null)
        {
            return Task.CompletedTask;
        }

        try
        {
            return session.TryChangePlaybackPositionAsync(Math.Max(0, position.Ticks)).AsTask();
        }
        catch
        {
            return Task.CompletedTask;
        }
    }

    private void RaiseTimeline(GlobalSystemMediaTransportControlsSession? session)
    {
        if (session is null || !ReferenceEquals(session, _session))
        {
            return;
        }

        try
        {
            TimelineChanged?.Invoke(ReadTimeline(session));
        }
        catch
        {
            // ignored
        }
    }

    /// <summary>
    /// SMTC only reports a timeline snapshot; while the track is playing the
    /// position has to be advanced by the time since that snapshot, otherwise
    /// the progress bar sits still between rare timeline updates.
    /// </summary>
    private static MediaTimeline ReadTimeline(GlobalSystemMediaTransportControlsSession session)
    {
        var t = session.GetTimelineProperties();
        var isPlaying = false;
        var playbackRate = 1.0;

        try
        {
            var playback = session.GetPlaybackInfo();
            isPlaying = playback.PlaybackStatus ==
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

            // Playback rate follows speed changes (podcasts, audiobooks, video).
            if (playback.PlaybackRate is { } rate
                && double.IsFinite(rate)
                && rate > 0
                && rate <= 16)
            {
                playbackRate = rate;
            }
        }
        catch
        {
            // Keep the raw snapshot if playback info is unavailable.
        }

        return AdvanceTimeline(
            t.Position,
            t.EndTime - t.StartTime,
            isPlaying,
            playbackRate,
            t.LastUpdatedTime,
            DateTimeOffset.Now);
    }

    internal static bool SameMedia(MediaInfo? left, MediaInfo? right)
    {
        if (left is null || right is null)
        {
            return ReferenceEquals(left, right);
        }

        return left.HasMedia == right.HasMedia
            && left.IsPlaying == right.IsPlaying
            && string.Equals(left.Title, right.Title, StringComparison.Ordinal)
            && string.Equals(left.Artist, right.Artist, StringComparison.Ordinal)
            && string.Equals(left.SourceApp, right.SourceApp, StringComparison.Ordinal)
            && ThumbnailEquals(left.Thumbnail, right.Thumbnail);
    }

    private static bool ThumbnailEquals(byte[]? left, byte[]? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null || left.Length != right.Length)
        {
            return false;
        }

        return left.AsSpan().SequenceEqual(right);
    }

    internal static MediaTimeline AdvanceTimeline(
        TimeSpan position,
        TimeSpan duration,
        bool isPlaying,
        double playbackRate,
        DateTimeOffset lastUpdated,
        DateTimeOffset now)
    {
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        if (isPlaying && lastUpdated != default)
        {
            var delta = now - lastUpdated;

            // A long gap is a sleep/resume or a clock change, not playback; the
            // next SMTC event re-syncs. Anything shorter is real listening time,
            // no matter how rarely the app publishes a timeline snapshot.
            if (delta > TimeSpan.Zero && delta < TimeSpan.FromHours(6))
            {
                position += TimeSpan.FromTicks((long)(delta.Ticks * playbackRate));
            }
        }

        if (position < TimeSpan.Zero)
        {
            position = TimeSpan.Zero;
        }

        if (duration > TimeSpan.Zero && position > duration)
        {
            position = duration;
        }

        return new MediaTimeline(position, duration);
    }

    private async Task<byte[]?> ResolveThumbnailAsync(
        GlobalSystemMediaTransportControlsSessionMediaProperties props,
        string appId)
    {
        var key = $"{props.Title}\u001F{props.Artist}\u001F{props.AlbumTitle}\u001F{appId}";
        var cache = Volatile.Read(ref _thumbnailCache);
        var now = DateTime.UtcNow;

        if (cache.Key == key)
        {
            // A new track can briefly still expose the previous cover. Keep
            // re-reading for a few seconds so late-published art replaces it.
            if (cache.Bytes is not null && now - cache.FirstSeenUtc >= ThumbnailSettleWindow)
            {
                return cache.Bytes;
            }

            if (cache.Bytes is null && now - cache.AttemptUtc < ThumbnailRetryDelay)
            {
                return null;
            }
        }

        var bytes = await ReadThumbnailAsync(props.Thumbnail);
        var firstSeen = cache.Key == key ? cache.FirstSeenUtc : now;
        Volatile.Write(ref _thumbnailCache, new ThumbnailCache(key, bytes, firstSeen, now));
        return bytes;
    }

    private static async Task<byte[]?> ReadThumbnailAsync(IRandomAccessStreamReference? thumbnail)
    {
        if (thumbnail is null)
        {
            return null;
        }

        try
        {
            using var stream = await thumbnail.OpenReadAsync();
            if (stream is null || stream.Size == 0 || stream.Size > MaxThumbnailBytes)
            {
                return null;
            }

            using var destination = new MemoryStream();
            using var source = stream.AsStreamForRead();
            await source.CopyToAsync(destination);
            var bytes = destination.ToArray();
            return bytes.Length == 0 ? null : bytes;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _metadataRetry.Dispose();
        _metadataPoll.Dispose();

        if (_session is not null)
        {
            _session.MediaPropertiesChanged -= OnProperties;
            _session.PlaybackInfoChanged -= OnPlayback;
            _session.TimelinePropertiesChanged -= OnTimeline;
            _session = null;
        }

        _manager = null;
    }

    private static string PrettyAppName(string appUserModelId)
    {
        if (string.IsNullOrWhiteSpace(appUserModelId))
        {
            return string.Empty;
        }

        var name = appUserModelId;
        var separator = name.LastIndexOfAny(new[] { '\\', '/' });
        if (separator >= 0 && separator + 1 < name.Length)
        {
            name = name[(separator + 1)..];
        }

        var bang = name.IndexOf('!');
        if (bang > 0)
        {
            name = name[..bang];
        }

        var dot = name.LastIndexOf('.');
        if (dot > 0 && name.EndsWith("exe", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..dot];
        }

        return name.Length == 0 ? name : char.ToUpperInvariant(name[0]) + name[1..].ToLowerInvariant();
    }
}
