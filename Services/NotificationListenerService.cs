using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Timer = System.Threading.Timer;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace TolkIsland.Services;

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

            await PollAsync(prime: true);

            if (_disposed)
            {
                return false;
            }

            // NotificationChanged is not available to unpackaged apps.
            _pollTimer = new Timer(
                _ => _ = PollSafelyAsync(),
                null,
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(1));

            return true;
        }
        catch
        {
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

            lock (_seenIds)
            {
                _seenIds.IntersectWith(present);
            }

            if (prime || fresh.Count == 0 || _disposed)
            {
                return;
            }

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
