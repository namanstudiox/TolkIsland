using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.Appointments;
using Timer = System.Threading.Timer;

namespace TolkIsland.Services;

public sealed record CalendarEvent(
    string Title,
    DateTimeOffset Start,
    DateTimeOffset End,
    string? JoinUrl)
{
    public string WhenLabel
    {
        get
        {
            var now = DateTimeOffset.Now;
            if (Start <= now && End >= now)
            {
                return "now";
            }

            var minutes = (int)Math.Round((Start - now).TotalMinutes);
            if (minutes is >= 0 and < 60)
            {
                return $"in {minutes} min";
            }

            return Start.LocalDateTime.ToString("HH:mm");
        }
    }
}

public enum CalendarAccess
{
    Unknown,
    Allowed,
    Denied
}

/// <summary>
/// Next calendar items from the Windows appointment store. Join URLs are
/// taken from the location/details text when they look like a meeting link.
/// </summary>
public sealed class CalendarService : IDisposable
{
    private static readonly Regex UrlPattern = new(
        @"https?://[^\s<>""]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly Timer _timer;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile bool _disposed;
    private IReadOnlyList<CalendarEvent> _events = Array.Empty<CalendarEvent>();
    private CalendarAccess _access;

    public CalendarService()
    {
        _timer = new Timer(_ => _ = RefreshAsync(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public event Action? Changed;

    public IReadOnlyList<CalendarEvent> Events => _events;

    public CalendarAccess Access => _access;

    public CalendarEvent? Next => _events.FirstOrDefault();

    public void Start()
    {
        if (_disposed || !SettingsService.Current.CalendarEnabled)
        {
            return;
        }

        _access = CalendarAccess.Unknown;
        _timer.Change(TimeSpan.FromSeconds(2), TimeSpan.FromMinutes(2));
        _ = RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        if (_disposed || !SettingsService.Current.CalendarEnabled)
        {
            return;
        }

        if (!await _gate.WaitAsync(0))
        {
            return;
        }

        try
        {
            var store = await AppointmentManager.RequestStoreAsync(AppointmentStoreAccessType.AllCalendarsReadOnly);
            if (store is null)
            {
                MarkDenied();
                return;
            }

            var start = DateTimeOffset.Now.AddMinutes(-15);
            var found = await store.FindAppointmentsAsync(start, TimeSpan.FromHours(18));
            var list = new List<CalendarEvent>();

            // Filter first, then cap: taking the first item before filtering could
            // pick an all-day or already-over entry over a real meeting.
            foreach (var item in found.OrderBy(a => a.StartTime))
            {
                if (item.AllDay)
                {
                    continue;
                }

                var end = item.StartTime + (item.Duration == default ? TimeSpan.FromMinutes(30) : item.Duration);
                if (end < DateTimeOffset.Now)
                {
                    continue;
                }

                // The island only ever shows the next event.
                if (list.Count == 1)
                {
                    break;
                }

                var details = $"{item.Location} {item.Details}";
                list.Add(new CalendarEvent(
                    string.IsNullOrWhiteSpace(item.Subject) ? "Event" : item.Subject.Trim(),
                    item.StartTime,
                    end,
                    FindJoinUrl(details)));
            }

            _events = list;
            _access = CalendarAccess.Allowed;
            Changed?.Invoke();
        }
        catch (UnauthorizedAccessException)
        {
            // Denied access must never render as an empty day.
            MarkDenied();
        }
        catch
        {
            // Transient store failures keep the last good events on screen.
        }
        finally
        {
            _gate.Release();
        }
    }

    private void MarkDenied()
    {
        _events = Array.Empty<CalendarEvent>();
        _access = CalendarAccess.Denied;
        Changed?.Invoke();
    }

    public void ApplySettings(bool enabled)
    {
        if (enabled)
        {
            Start();
            return;
        }

        _timer.Change(Timeout.Infinite, Timeout.Infinite);
        _events = Array.Empty<CalendarEvent>();
        Changed?.Invoke();
    }

    private static string? FindJoinUrl(string details)
    {
        foreach (Match match in UrlPattern.Matches(details))
        {
            // Trust the host, not a substring: evil.example/?next=zoom.us is not Zoom.
            var candidate = match.Value.TrimEnd('.', ',', ')', ']', ';', '!', '"', '\'');
            if (SafeUrl.IsTrustedMeeting(candidate, out var uri))
            {
                return uri!.ToString();
            }
        }

        return null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Dispose();
        _gate.Dispose();
    }
}
