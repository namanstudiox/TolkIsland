using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Timer = System.Threading.Timer;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace TolkIsland.Services;

/// <summary>Real Windows toast notifications via the UserNotificationListener API.</summary>
public sealed record NotificationInfo(
    string AppName,
    string Title,
    string Body,
    uint Id = 0,
    bool CanReply = false);

public sealed class NotificationListenerService : IDisposable
{
    private readonly HashSet<uint> _seenIds = new();
    private readonly SemaphoreSlim _pollGate = new(1, 1);
    private readonly SemaphoreSlim _startGate = new(1, 1);

    private UserNotificationListener? _listener;
    private Timer? _pollTimer;
    private volatile bool _disposed;
    private bool _resync;

    public bool IsAvailable { get; private set; }

    public event Action<NotificationInfo>? Arrived;

    public async Task<bool> StartAsync()
    {
        if (_disposed)
        {
            return false;
        }

        await _startGate.WaitAsync();
        try
        {
            if (_disposed)
            {
                return false;
            }

            // The listener is shared by every island window; only the first call
            // requests access and starts the poll timer.
            if (_listener is not null && _pollTimer is not null)
            {
                return IsAvailable;
            }

            return await StartLockedAsync();
        }
        finally
        {
            _startGate.Release();
        }
    }

    private async Task<bool> StartLockedAsync()
    {
        try
        {
            _listener = UserNotificationListener.Current;

            var access = await _listener.RequestAccessAsync();
            if (access != UserNotificationListenerAccessStatus.Allowed)
            {
                IsAvailable = false;
                return false;
            }

            if (_disposed)
            {
                return false;
            }

            IsAvailable = true;

            // Mark everything that already exists as seen so the island does not
            // replay old toasts every time the app starts.
            await PollAsync(prime: true);

            if (_disposed)
            {
                return false;
            }

            // UserNotificationListener.NotificationChanged throws for unpackaged
            // desktop apps on current Windows builds, so poll instead. It is cheap.
            _pollTimer = new Timer(
                _ => _ = PollSafelyAsync(),
                null,
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(1));

            return true;
        }
        catch
        {
            // Toast listening is only available when Windows allows it; otherwise the
            // island simply never enters the notification state instead of showing fake data.
            IsAvailable = false;
            return false;
        }
    }

    private async Task PollSafelyAsync()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            await PollAsync(prime: false);
        }
        catch
        {
            // A transient listener error must not take down the poll timer.
        }
    }

    private async Task PollAsync(bool prime)
    {
        if (_listener is null)
        {
            return;
        }

        if (!SettingsService.Current.NotificationsEnabled)
        {
            // No consumer: skip the WinRT round-trip entirely. The first poll after
            // the feature is re-enabled re-primes, so nothing arrives as "new".
            _resync = true;
            return;
        }

        await _pollGate.WaitAsync();
        try
        {
            if (_disposed)
            {
                return;
            }

            prime = prime || _resync;
            _resync = false;
            var toasts = await _listener.GetNotificationsAsync(NotificationKinds.Toast);
            var fresh = new List<UserNotification>();
            var present = new HashSet<uint>();

            foreach (var toast in toasts)
            {
                present.Add(toast.Id);

                // Only a toast we have never seen is new. This also stops a
                // notification that disappears from being shown again.
                bool isNew;
                lock (_seenIds)
                {
                    isNew = _seenIds.Add(toast.Id);
                }

                if (isNew && !prime)
                {
                    fresh.Add(toast);
                }
            }

            // Forget toasts that are no longer in the Action Center so an ID Windows
            // reuses later is not mistaken for an already-shown notification, and so
            // the set cannot grow forever.
            lock (_seenIds)
            {
                _seenIds.IntersectWith(present);
            }

            if (prime || fresh.Count == 0 || _disposed)
            {
                return;
            }

            // A poll can turn up several toasts at once. Show every one of them,
            // oldest first, instead of keeping only the newest and losing the rest.
            fresh.Sort((left, right) =>
            {
                var byTime = left.CreationTime.CompareTo(right.CreationTime);
                return byTime != 0 ? byTime : left.Id.CompareTo(right.Id);
            });

            foreach (var toast in fresh)
            {
                var info = ReadToast(toast);
                if (info is not null)
                {
                    Arrived?.Invoke(info);
                }
            }
        }
        catch
        {
            // ignored
        }
        finally
        {
            _pollGate.Release();
        }
    }

    private static NotificationInfo? ReadToast(UserNotification toast)
    {
        try
        {
            var appName = toast.AppInfo.DisplayInfo.DisplayName ?? string.Empty;
            var title = string.Empty;
            var body = string.Empty;

            var binding = toast.Notification.Visual.GetBinding(KnownNotificationBindings.ToastGeneric);
            var texts = binding?.GetTextElements();
            if (texts is not null)
            {
                if (texts.Count > 0)
                {
                    title = texts[0].Text ?? string.Empty;
                }

                if (texts.Count > 1)
                {
                    body = texts[1].Text ?? string.Empty;
                }
            }

            if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(body))
            {
                return null;
            }

            return new NotificationInfo(appName, title, body, toast.Id, CanReply: true);
        }
        catch
        {
            return null;
        }
    }

    public void Dismiss(uint id)
    {
        if (_listener is null || id == 0)
        {
            return;
        }

        try
        {
            _listener.RemoveNotification(id);
            lock (_seenIds)
            {
                _seenIds.Remove(id);
            }
        }
        catch
        {
            // Some toasts cannot be removed by a desktop listener.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _pollTimer?.Dispose();
        _pollTimer = null;
        IsAvailable = false;
    }
}
