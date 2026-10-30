using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows;
using System.Windows.Forms;
using Microsoft.Win32;
using TolkIsland.Services;
using Application = System.Windows.Application;

namespace TolkIsland;

public partial class App : Application
{
    private const string InstanceMutexName = @"Local\TolkIsland.SingleInstance";
    private const string ShowEventName = @"Local\TolkIsland.Show";
    private const string SettingsEventName = @"Local\TolkIsland.Settings";

    private readonly List<MainWindow> _islands = new();
    private NotifyIcon? _tray;
    private Icon? _trayIcon;
    private MainWindow? _island;
    private string _monitorKey = string.Empty;
    private string _monitorSignature = string.Empty;
    private bool _paused;
    private SettingsWindow? _settingsWindow;

    private Mutex? _instanceMutex;
    private EventWaitHandle? _showEvent;
    private EventWaitHandle? _settingsEvent;
    private CancellationTokenSource? _signals;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Local crash log first: anything below this line that throws still lands
        // in %LOCALAPPDATA%\TolkIsland\crash.log instead of dying silently.
        DiagnosticsService.Attach();

        var wantsSettings = e.Args.Any(arg =>
            string.Equals(arg, "--settings", StringComparison.OrdinalIgnoreCase));

        if (!TryAcquireSingleInstance(wantsSettings))
        {
            Shutdown();
            return;
        }

        if (wantsSettings)
        {
            OpenSettings();
            Shutdown();
            return;
        }

        // Island windows can be rebuilt when the monitor mode changes; that must
        // not end the process while the first window is briefly closed.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        BuildIslands();

        _trayIcon = LoadAppIcon();
        _tray = new NotifyIcon
        {
            Icon = _trayIcon,
            Visible = true,
            Text = "Tolk Island"
        };
        _tray.MouseUp += Tray_MouseUp;

        SettingsService.Changed += OnSettingsChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplayTopologyChanged;

        if (!SettingsService.Current.OnboardingCompleted)
        {
            Dispatcher.BeginInvoke(new Action(() => OpenSettings(isFirstRun: true)));
        }
    }

    /// <summary>
    /// One island per user session. A second launch (shortcut, Run key, installer
    /// finish page) signals the running copy instead of stacking overlays, tray
    /// icons and a second global hotkey registration.
    /// </summary>
    private bool TryAcquireSingleInstance(bool wantsSettings)
    {
        try
        {
            _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out var isFirstInstance);
            if (!isFirstInstance)
            {
                _instanceMutex.Dispose();
                _instanceMutex = null;
                SignalRunningInstance(wantsSettings);
                return false;
            }
        }
        catch
        {
            // Policy can deny the mutex; running twice beats not running at all.
            _instanceMutex = null;
        }

        if (wantsSettings)
        {
            // A settings-only run must not hold the single-instance mutex: a
            // normal launch while it is open would signal a process with no island.
            try
            {
                _instanceMutex?.ReleaseMutex();
            }
            catch
            {
                // Not owning the mutex is harmless here.
            }

            _instanceMutex?.Dispose();
            _instanceMutex = null;
            return true;
        }

        ListenForSignals();
        return true;
    }

    private static void SignalRunningInstance(bool openSettings)
    {
        try
        {
            using var handle = EventWaitHandle.OpenExisting(
                openSettings ? SettingsEventName : ShowEventName);
            handle.Set();
        }
        catch
        {
            // The other instance is already shutting down.
        }
    }

    private void ListenForSignals()
    {
        try
        {
            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            _settingsEvent = new EventWaitHandle(false, EventResetMode.AutoReset, SettingsEventName);
            _signals = new CancellationTokenSource();
            var token = _signals.Token;
            var show = _showEvent;
            var settings = _settingsEvent;

            var listener = new Thread(() =>
            {
                var handles = new WaitHandle[] { show, settings };
                while (!token.IsCancellationRequested)
                {
                    int index;
                    try
                    {
                        index = WaitHandle.WaitAny(handles, TimeSpan.FromSeconds(1));
                    }
                    catch (ObjectDisposedException)
                    {
                        return;
                    }
                    catch
                    {
                        return;
                    }

                    if (index == WaitHandle.WaitTimeout || token.IsCancellationRequested)
                    {
                        continue;
                    }

                    var openSettings = index == 1;
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (openSettings)
                        {
                            OpenSettings();
                            return;
                        }

                        _island?.ShowFromTray();
                    }));
                }
            })
            {
                IsBackground = true,
                Name = "TolkIsland single-instance signals"
            };

            listener.Start();
        }
        catch
        {
            // Named events are best-effort; the first instance still works.
        }
    }

    private static Icon LoadAppIcon()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(path))
            {
                var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
                if (icon is not null)
                {
                    return icon;
                }
            }
        }
        catch
        {
            // Fall through to the stock icon.
        }

        return System.Drawing.SystemIcons.Application;
    }

    private void OpenSettings(bool isFirstRun = false)
    {
        if (_settingsWindow is { IsVisible: true } existing)
        {
            existing.Activate();
            return;
        }

        var window = new SettingsWindow(isFirstRun);
        _settingsWindow = window;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_settingsWindow, window))
            {
                _settingsWindow = null;
            }
        };

        try
        {
            window.ShowDialog();
        }
        finally
        {
            if (ReferenceEquals(_settingsWindow, window))
            {
                _settingsWindow = null;
            }
        }
    }

    /// <summary>
    /// Creates one shell for the chosen monitor, or one per screen in all-monitor
    /// mode. Exactly one shell is primary and owns the shared Windows services.
    /// </summary>
    private void BuildIslands()
    {
        var settings = SettingsService.Current;
        _monitorKey = $"{settings.MonitorMode}|{settings.MonitorDeviceName}";

        if (settings.MonitorMode == MonitorMode.All)
        {
            foreach (var screen in Screen.AllScreens.OrderBy(screen => screen.Primary ? 0 : 1))
            {
                var island = new MainWindow(screen.DeviceName, screen.Primary) { ShowActivated = false };
                _islands.Add(island);
                if (island.IsPrimary)
                {
                    _island ??= island;
                }
            }
        }
        else if (settings.MonitorMode == MonitorMode.Specific
                 && !string.IsNullOrWhiteSpace(settings.MonitorDeviceName))
        {
            _island = new MainWindow(settings.MonitorDeviceName, true) { ShowActivated = false };
            _islands.Add(_island);
        }
        else
        {
            _island = new MainWindow { ShowActivated = false };
            _islands.Add(_island);
        }

        // Never leave the app without a shell if a monitor name went stale.
        _island ??= _islands.FirstOrDefault();
        MainWindow = _island;

        _monitorSignature = MonitorSignature();

        foreach (var island in _islands)
        {
            island.PauseRequested = SetPausedEverywhere;
            island.Show();

            // A rebuild must not silently resume a paused island: the new shell
            // would otherwise restart capture and notifications on its own.
            if (_paused)
            {
                island.SetPaused(true);
            }
        }
    }

    /// <summary>
    /// Identity of the current monitor set, including which screen is primary:
    /// the all-monitor mode needs one shell per entry, and the primary flag
    /// decides which shell owns the shared services.
    /// </summary>
    private static string MonitorSignature()
        => string.Join("|", Screen.AllScreens
            .OrderBy(screen => screen.DeviceName, StringComparer.OrdinalIgnoreCase)
            .Select(screen => screen.DeviceName + (screen.Primary ? "*" : "")));

    /// <summary>
    /// Docking or undocking a display changes how many islands should exist.
    /// Single-shell modes re-target themselves on the same event, so only the
    /// all-monitor mode needs to rebuild, and only when the set actually moved.
    /// </summary>
    private void OnDisplayTopologyChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.HasShutdownStarted)
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => OnDisplayTopologyChanged(sender, e)));
            return;
        }

        if (SettingsService.Current.MonitorMode != MonitorMode.All)
        {
            return;
        }

        if (string.Equals(MonitorSignature(), _monitorSignature, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        RebuildIslands();
    }

    private void OnSettingsChanged()
    {
        var settings = SettingsService.Current;
        var key = $"{settings.MonitorMode}|{settings.MonitorDeviceName}";
        if (string.Equals(key, _monitorKey, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // Defer: rebuilding inside the Changed dispatch would close windows that
        // are halfway through their own settings handler.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            var current = SettingsService.Current;
            var latest = $"{current.MonitorMode}|{current.MonitorDeviceName}";
            if (string.Equals(latest, _monitorKey, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            RebuildIslands();
        }));
    }

    private void RebuildIslands()
    {
        foreach (var window in _islands.ToList())
        {
            window.Close();
        }

        _islands.Clear();
        _island = null;
        BuildIslands();
    }

    private void Tray_MouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right)
        {
            return;
        }

        TrayMenuWindow.ShowMenu(BuildTrayMenuItems());
    }

    private List<TrayMenuItem> BuildTrayMenuItems()
    {
        var paused = _island?.IsPaused ?? false;

        return new List<TrayMenuItem>
        {
            new()
            {
                Label = "Show / hide island",
                Glyph = "\uE7B3",
                Shortcut = SettingsService.Current.Hotkey,
                Invoke = () => _island?.ToggleIsland()
            },
            new()
            {
                Label = "Pause island",
                Glyph = "\uE769",
                IsOn = paused,
                Invoke = () => SetPausedEverywhere(!paused)
            },
            new()
            {
                Label = "Settings…",
                Glyph = "\uE713",
                Invoke = () => OpenSettings()
            },
            new()
            {
                Label = "Exit",
                Glyph = "\uE8BB",
                IsDanger = true,
                Invoke = ExitApp
            }
        };
    }

    /// <summary>Pause is process-wide: every screen keeps the same state.</summary>
    private void SetPausedEverywhere(bool paused)
    {
        _paused = paused;

        foreach (var island in _islands)
        {
            island.SetPaused(paused);
        }
    }

    private void ExitApp()
    {
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SettingsService.Changed -= OnSettingsChanged;
        SystemEvents.DisplaySettingsChanged -= OnDisplayTopologyChanged;

        // Stop the single-instance listener before its handles go away.
        _signals?.Cancel();
        _showEvent?.Dispose();
        _settingsEvent?.Dispose();
        _signals?.Dispose();
        _signals = null;

        if (_instanceMutex is not null)
        {
            try
            {
                _instanceMutex.ReleaseMutex();
            }
            catch
            {
                // Not the owner (mutex creation failed earlier); nothing to release.
            }

            _instanceMutex.Dispose();
            _instanceMutex = null;
        }

        // Only tear down the shared Windows services if a shell actually started
        // them (the --settings entry point never does).
        if (_islands.Count > 0)
        {
            IslandServices.Shared.Shutdown();
        }

        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
            _tray = null;
        }

        base.OnExit(e);
    }
}
