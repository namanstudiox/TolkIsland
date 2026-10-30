using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Windows.Media;
using TolkIsland.Native;
using TolkIsland.Services;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using Cursors = System.Windows.Input.Cursors;
using DataFormats = System.Windows.DataFormats;
using DataObject = System.Windows.DataObject;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using FontFamily = System.Windows.Media.FontFamily;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MediaTimeline = TolkIsland.Services.MediaTimeline;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using MouseWheelEventArgs = System.Windows.Input.MouseWheelEventArgs;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using SystemColors = System.Windows.SystemColors;

namespace TolkIsland;

public enum IslandState
{
    Idle,
    IdleHidden,
    IdleExpanded,
    CompactMedia,
    ExpandedMedia,
    CompactFocus,
    FocusPicker,
    FocusTimer,
    FocusComplete,
    Notification,
    Charging,
    Volume,
    Brightness,
    Shelf,
    Notes,
    Calendar,
    Prompt,
    Mirror,
    LiveActivity
}

/// <summary>
/// A Windows Dynamic Island. The host window is a fixed size and always topmost;
/// only the black shell inside it morphs, so transitions stay smooth. Mouse input
/// is controlled per-pixel through WM_NCHITTEST: outside the shell the overlay is
/// fully click-through.
/// </summary>
public partial class MainWindow : Window
{
    // Shell sizes (DIPs) include the 20 px ears on each side; the content
    // itself is inset back to the original body width. The window never changes size.
    private const double EarRadius = 20;
    private const double BottomRadius = 30;
    private const double IdleWidth = 300;
    private const double IdleHeight = 46;
    private const double IdleHiddenWidth = 160;
    private const double IdleHiddenHeight = 10;
    private const double IdleExpandedWidth = 500;
    private const double IdleExpandedHeight = 172;
    private const double CompactMediaWidth = 300;
    private const double CompactMediaHeight = 50;
    private const double ExpandedMediaWidth = 460;
    private const double ExpandedMediaHeight = 172;
    private const double CompactFocusWidth = 300;
    private const double CompactFocusHeight = 50;
    private const double FocusPickerWidth = 460;
    private const double FocusPickerHeight = 168;
    private const double TimerWidth = 460;
    private const double TimerHeight = 160;
    private const double FocusCompleteWidth = 460;
    private const double FocusCompleteHeight = 88;
    private const double NotificationWidth = 460;
    private const double NotificationHeight = 118;
    private const double ChargingWidth = 380;
    private const double ChargingHeight = 72;
    private const double VolumeWidth = 440;
    private const double VolumeHeight = 72;
    private const double BrightnessWidth = 440;
    private const double BrightnessHeight = 72;
    private const double ShelfWidth = 460;
    private const double ShelfHeight = 190;
    private const double NotesWidth = 460;
    private const double NotesHeight = 208;
    private const double CalendarWidth = 460;
    private const double CalendarHeight = 150;
    private const double PromptWidth = 460;
    private const double PromptHeight = 172;
    private const double MirrorWidth = 480;
    private const double MirrorHeight = 280;
    private const double LiveActivityWidth = 460;
    private const double LiveActivityHeight = 88;

    // Expansion is a spring that settles; collapse is a quick retreat. Both are
    // short enough to feel like one gesture with the click that caused them.
    private static readonly TimeSpan NormalExpandDuration = TimeSpan.FromMilliseconds(340);
    private static readonly TimeSpan NormalCollapseDuration = TimeSpan.FromMilliseconds(260);
    private static readonly TimeSpan NormalContentFade = TimeSpan.FromMilliseconds(180);
    private static readonly TimeSpan NormalHoverLeaveDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan SplashDuration = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan NotificationVisibleDuration = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan ChargingVisibleDuration = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan VolumeVisibleDuration = TimeSpan.FromSeconds(1.6);
    private static readonly TimeSpan BrightnessVisibleDuration = TimeSpan.FromSeconds(1.8);
    private static readonly TimeSpan FocusCompleteVisibleDuration = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan LiveActivityVisibleDuration = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan IdleHideDelay = TimeSpan.FromSeconds(10);

    private const int HotKeyId = 0x4A49;
    private const int WM_NCHITTEST = 0x0084;
    private const int HTCLIENT = 1;
    private const int HTTRANSPARENT = -1;

    private const double VolumeTrackWidth = 250;
    private const double ChargingBatteryMaxWidth = 22;
    private const double WaveformFloor = 0.16;

    private static readonly SolidColorBrush DangerAccentBrush = MakeBrush("#FFFF453A");
    private static readonly SolidColorBrush WarningAccentBrush = MakeBrush("#FFFFD60A");

    private readonly MediaSessionService _media;
    private readonly NotificationListenerService _notifications;
    private readonly FocusTimerService _focusTimer;
    private readonly AudioVolumeService _audio;
    private readonly AudioVisualizerService _visualizer;
    private readonly BrightnessService _brightness;
    private readonly ShelfService _shelf;
    private readonly LocalApiService _localApi;
    private readonly MirrorService _mirror;
    private readonly NotesService _notes;
    private readonly CalendarService _calendar;
    private readonly DownloadWatchService _downloads;
    private readonly string? _boundDeviceName;
    private readonly bool _isPrimary;

    // One 1 s heartbeat drives the clock, battery, idle retract and fullscreen
    // check. Four separate timers woke the dispatcher at four different phases
    // for work that all costs less than a frame.
    private readonly DispatcherTimer _heartbeat = new();
    private readonly DispatcherTimer _timeline = new();
    private readonly DispatcherTimer _hoverLeave = new();
    private readonly DispatcherTimer _splash = new();
    private readonly DispatcherTimer _notificationCountdown = new();
    private readonly DispatcherTimer _chargingCountdown = new();
    private readonly DispatcherTimer _volumeCountdown = new();
    private readonly DispatcherTimer _brightnessCountdown = new();
    private readonly DispatcherTimer _focusCompleteCountdown = new();
    private readonly DispatcherTimer _waveform = new();
    private readonly DispatcherTimer _liveActivityCountdown = new();
    private readonly List<Border> _waveformBars = new();
    private readonly float[] _waveformLevels = new float[AudioVisualizerService.BandCount];

    private AppSettings _settings = SettingsService.Current;
    private IslandPreview? _preview;
    private bool _previewActive;
    private double _brightnessLevel;
    private string _liveActivityTitle = string.Empty;
    private string _liveActivityBody = string.Empty;
    private string _liveActivityGlyph = "\uE896";
    private double? _liveActivityFraction;
    private bool _highContrast;
    private readonly PromptRequest _promptRequest = new();
    private CancellationTokenSource? _promptCancel;
    private Point _shelfDragStart;
    private string? _shelfDragPath;
    private bool _seeking;
    private bool _applyingSettings;
    private bool _captureWanted;
    private bool _positionReady;
    private string _quickLaunchSignature = string.Empty;
    private int _heartbeatTicks;
    private byte[]? _artworkBytes;
    private ImageSource? _artworkSource;
    private int _mirrorFramePending;
    private bool _mirrorFrameShown;
    private bool _mirrorWanted;
    private bool _waveformResting = true;
    private readonly System.Text.StringBuilder _classNameBuffer = new(256);
    private string? _lastTargetScreen;
    private bool _displayEventsHooked;

    private IslandState _state = IslandState.Idle;
    private MediaInfo? _currentMedia;
    private NotificationInfo? _pendingNotification;
    private readonly NotificationQueue<(NotificationInfo Info, TimeSpan Duration)> _notificationQueue = new(3);
    private IslandState? _overlayReturnState;
    private TimeSpan _position;
    private TimeSpan _duration;
    private bool _splashActive;
    private FocusTimerRunState _lastFocusStatus = FocusTimerRunState.Stopped;
    private int _focusCompletedMinutes;
    private int _fullscreenTicks;
    private double _dpiScale = 1.0;
    private bool? _lastOnAc;
    private double _chargingPercent;
    private double _volumeLevel;
    private bool _muted;
    private bool _quietMode;
    private bool _pinned;
    private volatile bool _paused;
    private bool _idleHideEnabled = true;
    private DateTime _lastPresence = DateTime.UtcNow;
    private IntPtr _windowHandle;
    private IntPtr _previousForeground;
    private bool _hotkeyRegistered;
    private IntPtr _mouseHook;
    private NativeMethods.LowLevelMouseProc? _mouseHookProc;

    public MainWindow(string? boundDeviceName = null, bool isPrimary = true)
    {
        var runtime = IslandServices.Shared;
        _media = runtime.Media;
        _notifications = runtime.Notifications;
        _focusTimer = runtime.FocusTimer;
        _audio = runtime.Audio;
        _visualizer = runtime.Visualizer;
        _brightness = runtime.Brightness;
        _shelf = runtime.Shelf;
        _localApi = runtime.LocalApi;
        _mirror = runtime.Mirror;
        _notes = runtime.Notes;
        _calendar = runtime.Calendar;
        _downloads = runtime.Downloads;
        _boundDeviceName = boundDeviceName;
        _isPrimary = isPrimary;

        _settings = SettingsService.Current;
        _quietMode = _settings.QuietMode;
        _idleHideEnabled = _settings.IdleHide;

        InitializeComponent();
        ApplyInitialSettings();
    }

    public bool IsPrimary => _isPrimary;

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateShellShape();
        PlaySpiderBuddySwing();
        PositionWindow(force: true);

        var helper = new WindowInteropHelper(this);
        _windowHandle = helper.Handle;
        if (_windowHandle != IntPtr.Zero)
        {
            ApplyNativeWindowStyle(_windowHandle);
            HwndSource.FromHwnd(_windowHandle)?.AddHook(WndProc);
            if (_isPrimary)
            {
                RegisterConfiguredHotkey();
            }
        }

        Shell.AddHandler(
            UIElement.MouseLeftButtonUpEvent,
            new MouseButtonEventHandler(Shell_MouseLeftButtonUp),
            handledEventsToo: true);

        BuildWaveform();

        _heartbeat.Interval = TimeSpan.FromSeconds(1);
        _heartbeat.Tick += (_, _) => OnHeartbeat();
        _heartbeat.Start();
        UpdateClock();
        UpdateBattery();

        _timeline.Interval = TimeSpan.FromMilliseconds(500);
        _timeline.Tick += (_, _) =>
        {
            // No session: no WinRT call, no layout pass, every half second.
            if (_currentMedia?.HasMedia != true)
            {
                return;
            }

            var t = _media.GetTimeline();
            if (t.HasValue)
            {
                _position = t.Value.Position;
                _duration = t.Value.Duration;
            }

            UpdateProgressVisual();
        };

        _hoverLeave.Interval = NormalHoverLeaveDelay;
        _hoverLeave.Tick += HoverLeave_Tick;

        _splash.Interval = SplashDuration;
        _splash.Tick += Splash_Tick;

        _notificationCountdown.Interval = NotificationVisibleDuration;
        _notificationCountdown.Tick += (_, _) => FinishOverlay(IslandState.Notification);

        _chargingCountdown.Interval = ChargingVisibleDuration;
        _chargingCountdown.Tick += (_, _) => FinishOverlay(IslandState.Charging);

        _volumeCountdown.Interval = VolumeVisibleDuration;
        _volumeCountdown.Tick += (_, _) => FinishOverlay(IslandState.Volume);

        _brightnessCountdown.Interval = BrightnessVisibleDuration;
        _brightnessCountdown.Tick += (_, _) => FinishOverlay(IslandState.Brightness);

        _focusCompleteCountdown.Interval = FocusCompleteVisibleDuration;
        _focusCompleteCountdown.Tick += (_, _) => FinishOverlay(IslandState.FocusComplete);

        // 30 fps read of the WASAPI loopback spectrum. The service does its own
        // attack/release smoothing, so the tick can set ScaleY directly.
        _waveform.Interval = TimeSpan.FromMilliseconds(33);
        _waveform.Tick += (_, _) => PulseWaveform();

        _liveActivityCountdown.Interval = LiveActivityVisibleDuration;
        _liveActivityCountdown.Tick += (_, _) => FinishOverlay(IslandState.LiveActivity);

        _media.CurrentChanged += OnMediaChanged;
        _media.TimelineChanged += OnTimelineChanged;
        _notifications.Arrived += OnNotificationArrived;
        _focusTimer.StateChanged += OnFocusTimerStateChanged;
        _focusTimer.ElapsedChanged += OnFocusTimerElapsed;
        _audio.VolumeChanged += OnVolumeChanged;
        _brightness.BrightnessChanged += OnBrightnessChanged;
        _shelf.Changed += OnShelfChanged;
        _mirror.StateChanged += OnMirrorStateChanged;
        _mirror.FrameReady += OnMirrorFrameReady;
        _notes.Changed += OnNotesChanged;
        _calendar.Changed += OnCalendarChanged;
        _downloads.FileArrived += OnDownloadArrived;
        _downloads.TransferProgress += OnTransferProgress;
        SettingsService.Changed += OnSettingsChanged;
        SettingsService.Previewed += OnSettingsPreview;
        SettingsService.PreviewEnded += OnSettingsPreviewEnded;

        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
        _displayEventsHooked = true;

        if (_isPrimary)
        {
            _localApi.PausedProvider = () => _paused;
            _localApi.FeatureProvider = feature => feature switch
            {
                "media" => _settings.MediaEnabled,
                "focus" => _settings.FocusEnabled,
                "shelf" => _settings.ShelfEnabled,
                "notifications" => _settings.NotificationsEnabled,
                _ => true
            };
            _localApi.ActivityReceived += OnApiActivity;
            _localApi.ShelfRequested += OnApiShelf;
            _localApi.PanelRequested += OnApiPanel;
            _localApi.MediaCommandRequested += OnApiMedia;
            _localApi.FocusRequested += OnApiFocus;

            _ = _media.StartAsync();
            _ = _notifications.StartAsync();
            _audio.Start();
            _brightness.Start();
            _localApi.Start();
            _calendar.Start();
            _downloads.Start();
        }

        BuildShelfItems();
        BuildNotesItems();
        UpdateTimerVisual();
        RenderCalendarPanel();
        BuildQuickLaunch();

        // A window created after playback started (display hot-plug, all-monitor
        // mode) seeds from the shared snapshot instead of waiting for an event.
        if (_media.Current is { HasMedia: true } currentMedia)
        {
            _currentMedia = currentMedia;
            if (_settings.MediaEnabled && !_paused)
            {
                // Resolve instead of assuming media: a focus timer may already be
                // running, and MediaFirst decides which compact bar comes first.
                Transition(ResolveResting());
            }
        }
    }

    // ---------------------------------------------------------------- placement

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);

        // WM_NCHITTEST works in physical pixels; refresh the scale and recenter
        // so the clickable silhouette still lines up after a scaling change.
        _dpiScale = newDpi.DpiScaleX > 0 ? newDpi.DpiScaleX : 1;
        Dispatcher.BeginInvoke(new Action(() => PositionWindow(force: true)));
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.HasShutdownStarted)
        {
            return;
        }

        Dispatcher.BeginInvoke(new Action(() => PositionWindow(force: true)));
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General
            or UserPreferenceCategory.Color
            or UserPreferenceCategory.VisualStyle)
        {
            ReapplySystemAppearance();
        }
    }

    private void OnSystemParametersChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName)
            || e.PropertyName == nameof(SystemParameters.HighContrast))
        {
            ReapplySystemAppearance();
        }
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (Dispatcher.HasShutdownStarted)
        {
            return;
        }

        if (e.Reason is not (SessionSwitchReason.SessionLock
            or SessionSwitchReason.SessionLogoff
            or SessionSwitchReason.ConsoleDisconnect
            or SessionSwitchReason.RemoteDisconnect))
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => OnSessionSwitch(sender, e)));
            return;
        }

        // Locking or disconnecting the session must never leave the camera on.
        if (_state == IslandState.Mirror)
        {
            Transition(ResolveResting());
        }
        else
        {
            _mirrorWanted = false;
            _mirror.SetActive(this, false);
        }
    }

    /// <summary>
    /// A Windows theme or contrast switch must repaint immediately; the island
    /// covers the top of the screen and a restart is not something the user
    /// should need to discover.
    /// </summary>
    private void ReapplySystemAppearance()
    {
        if (Dispatcher.HasShutdownStarted)
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(ReapplySystemAppearance));
            return;
        }

        ApplyAppearance();
        UpdateShellShape();
        RenderState();
    }

    private void PositionWindow(bool force = false)
    {
        if (!_positionReady && _windowHandle == IntPtr.Zero && !IsLoaded)
        {
            return;
        }

        var screen = ResolveTargetScreen();
        if (screen is null)
        {
            return;
        }

        if (!force && string.Equals(_lastTargetScreen, screen.DeviceName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _lastTargetScreen = screen.DeviceName;

        var bounds = screen.Bounds;
        var work = screen.WorkingArea;
        var dpi = GetScreenDpi(screen, bounds);
        _dpiScale = dpi;

        var positionMode = _preview?.PositionMode ?? _settings.PositionMode;
        var topOffset = _preview?.TopOffset ?? _settings.TopOffset;
        var horizontalOffset = _preview?.HorizontalOffset ?? _settings.HorizontalOffset;

        var physicalWidth = Width * dpi;
        var physicalHeight = Height * dpi;
        var x = bounds.Left + (bounds.Width - physicalWidth) / 2.0;
        var y = (double)bounds.Top;

        if (positionMode is PositionMode.TopOffset or PositionMode.FloatingPill)
        {
            y = work.Top + topOffset * dpi;
        }

        if (positionMode == PositionMode.FloatingPill)
        {
            y = work.Top + Math.Max(8, topOffset) * dpi;
        }

        x += horizontalOffset * dpi;

        // SetWindowPos works in physical pixels. Positioning the WPF window via
        // Left/Top can drift when monitors have different DPI scales; this keeps
        // the native window and the WM_NCHITTEST math on the same coordinates.
        if (_windowHandle != IntPtr.Zero)
        {
            var flags = NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE;
            if (Visibility == Visibility.Visible)
            {
                // A forced reposition can fire while the island is hidden for a
                // fullscreen app or a pause; never let it bring the HWND back.
                flags |= NativeMethods.SWP_SHOWWINDOW;
            }

            NativeMethods.SetWindowPos(
                _windowHandle,
                new IntPtr(-1),
                (int)Math.Round(x),
                (int)Math.Round(y),
                0,
                0,
                flags);
        }
        else
        {
            SetCurrentValue(LeftProperty, x / dpi);
            SetCurrentValue(TopProperty, y / dpi);
        }

        _positionReady = true;
    }

    private System.Windows.Forms.Screen? ResolveTargetScreen()
    {
        var screens = System.Windows.Forms.Screen.AllScreens;
        if (screens.Length == 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(_boundDeviceName))
        {
            var bound = screens.FirstOrDefault(screen => string.Equals(
                screen.DeviceName,
                _boundDeviceName,
                StringComparison.OrdinalIgnoreCase));
            if (bound is not null)
            {
                return bound;
            }
        }

        if (_settings.MonitorMode == MonitorMode.Specific
            && !string.IsNullOrWhiteSpace(_settings.MonitorDeviceName))
        {
            var specific = screens.FirstOrDefault(screen => string.Equals(
                screen.DeviceName,
                _settings.MonitorDeviceName,
                StringComparison.OrdinalIgnoreCase));
            if (specific is not null)
            {
                return specific;
            }
        }

        if (_settings.MonitorMode == MonitorMode.ActiveWindow)
        {
            var foreground = NativeMethods.GetForegroundWindow();
            if (foreground != IntPtr.Zero)
            {
                try
                {
                    return System.Windows.Forms.Screen.FromHandle(foreground);
                }
                catch
                {
                    // Fall through to the primary screen.
                }
            }
        }

        return System.Windows.Forms.Screen.PrimaryScreen ?? screens[0];
    }

    private static double GetScreenDpi(System.Windows.Forms.Screen screen, System.Drawing.Rectangle bounds)
    {
        try
        {
            var monitor = NativeMethods.MonitorFromPoint(
                new NativeMethods.POINT { X = bounds.Left + 1, Y = bounds.Top + 1 },
                NativeMethods.MONITOR_DEFAULTTONEAREST);

            if (monitor != IntPtr.Zero
                && NativeMethods.GetDpiForMonitor(monitor, 0, out var dpiX, out _) == 0
                && dpiX > 0)
            {
                return dpiX / 96.0;
            }
        }
        catch
        {
            // shcore.dll is missing on very old builds; WPF's scale is the fallback.
        }

        return 1.0;
    }

    private void ApplyNativeWindowStyle(IntPtr hwnd)
    {
        var ex = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
        ex |= NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW;
        NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE, ex);

        PinTopMost(hwnd);
    }

    private void PinTopMost(IntPtr hwnd)
        => NativeMethods.SetWindowPos(
            hwnd,
            new IntPtr(-1),
            0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);

    private void ApplyInitialSettings()
    {
        _settings = SettingsService.Current;
        _quietMode = _settings.QuietMode;
        _idleHideEnabled = _settings.IdleHide;

        ApplyMirrorDock();
        ApplyAppearance();

        var (width, height) = SizeFor(IslandState.Idle);
        Shell.Width = width;
        Shell.Height = height;
        UpdateShellShape();
    }

    private void OnSettingsChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(OnSettingsChanged));
            return;
        }

        if (_applyingSettings)
        {
            return;
        }

        var oldHotkey = _settings.Hotkey;
        var oldIdleHide = _settings.IdleHide;
        _preview = null;
        _settings = SettingsService.Current;
        RefreshPromptPrivacy();

        if (!_settings.MediaEnabled)
        {
            SyncCapture();
        }

        _applyingSettings = true;
        try
        {
            _quietMode = _settings.QuietMode;
            _idleHideEnabled = _settings.IdleHide;
            QuietGlyph.Visibility = _quietMode ? Visibility.Visible : Visibility.Collapsed;
            ApplyMirrorDock();
            ApplyAppearance();
            _hoverLeave.Interval = NormalHoverLeaveDelay;
            BuildQuickLaunch();

            if (_isPrimary)
            {
                _calendar.ApplySettings(_settings.CalendarEnabled);
                _downloads.ApplySettings(_settings.DownloadsEnabled);
            }

            Shell.BeginAnimation(WidthProperty, null);
            Shell.BeginAnimation(HeightProperty, null);
            var (width, height) = SizeFor(_state);
            Shell.Width = width;
            Shell.Height = height;
            UpdateShellShape();
            RenderState();

            PositionWindow(force: true);

            if (!_settings.IdleHide && _state == IslandState.IdleHidden)
            {
                Transition(IslandState.Idle);
            }
            else if (oldIdleHide != _settings.IdleHide)
            {
                _lastPresence = DateTime.UtcNow;
            }

            if (!string.Equals(oldHotkey, _settings.Hotkey, StringComparison.OrdinalIgnoreCase))
            {
                RegisterConfiguredHotkey();
            }
        }
        finally
        {
            _applyingSettings = false;
        }
    }

    private void OnSettingsPreview(IslandPreview preview)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => OnSettingsPreview(preview)));
            return;
        }

        _preview = preview;
        _previewActive = true;

        // The island may be idle-hidden while the user tweaks settings; reveal it
        // so the live preview is actually visible.
        if (_state is IslandState.Idle or IslandState.IdleHidden)
        {
            Transition(IslandState.IdleExpanded);
        }

        _lastPresence = DateTime.UtcNow;
        ApplyAppearance();
        ResizeShellImmediately();
        PositionWindow(force: true);
    }

    private void OnSettingsPreviewEnded()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(OnSettingsPreviewEnded));
            return;
        }

        if (!_previewActive)
        {
            return;
        }

        _previewActive = false;
        _preview = null;
        ApplyAppearance();
        ResizeShellImmediately();
        PositionWindow(force: true);

        if (!_pinned && !Shell.IsMouseOver && _state == IslandState.IdleExpanded)
        {
            Transition(ResolveResting());
        }
    }

    private void ResizeShellImmediately()
    {
        var (width, height) = SizeFor(_state);
        Shell.BeginAnimation(WidthProperty, null);
        Shell.BeginAnimation(HeightProperty, null);
        Shell.Width = width;
        Shell.Height = height;
        UpdateShellShape();
    }

    private void ApplyAppearance()
    {
        var theme = _preview?.Theme ?? _settings.Theme;
        var skin = _preview?.Skin ?? _settings.Skin;
        var spider = skin == SkinMode.Spider;
        var userAccent = _preview?.AccentColor ?? _settings.AccentColor;
        var highContrast = (_preview?.HighContrast ?? _settings.HighContrast) || SystemParameters.HighContrast;
        var dark = theme switch
        {
            ThemeMode.Light => false,
            ThemeMode.System => !IsSystemLightTheme(),
            _ => true
        };
        var accentColor = SpiderSkin.AccentFor(skin, dark, userAccent);

        _highContrast = highContrast;
        ButtonMotion.ReducedMotion = ReducedMotion;

        var buddyScale = _preview?.Scale ?? _settings.Scale;
        SpiderBuddyImage.Source = SpiderSkin.BuildBuddy(dark);
        SpiderBuddy.Width = SpiderSkin.BuddyWidth * buddyScale;
        SpiderBuddy.Height = SpiderSkin.BuddyHeight * buddyScale;
        SpiderBuddy.Margin = new Thickness(0, 0, 0, -SpiderSkin.BuddyHeight * buddyScale);

        if (highContrast)
        {
            Resources["PrimaryBrush"] = SystemColors.WindowTextBrush;
            Resources["SecondaryBrush"] = SystemColors.GrayTextBrush;
            Resources["ChipBrush"] = SystemColors.ControlBrush;
            Resources["TrackBrush"] = SystemColors.ControlDarkBrush;
            Resources["BlueBrush"] = SystemColors.HighlightBrush;
            Resources["GreenBrush"] = SystemColors.HighlightBrush;
            Resources["WaveBrush"] = SystemColors.HighlightBrush;
            Resources["AccentInkBrush"] = SystemColors.HighlightTextBrush;
            Resources["AccentTextBrush"] = SystemColors.HighlightBrush;
            Resources["GreenInkBrush"] = SystemColors.HighlightTextBrush;
            Resources["HoverBrush"] = SystemColors.HighlightBrush;
            Resources["PressedBrush"] = SystemColors.ControlDarkBrush;
            Resources["TextHoverBrush"] = SystemColors.HighlightTextBrush;
            Resources["CircleBorderBrush"] = SystemColors.WindowTextBrush;
            Resources["ShellBorderBrush"] = SystemColors.WindowTextBrush;
            Resources["HomeTileBrush"] = SystemColors.ControlBrush;
            Resources["HairlineBrush"] = SystemColors.ControlDarkBrush;
            Resources["CardBrush"] = SystemColors.ControlBrush;
            Resources["CardBorderBrush"] = SystemColors.WindowTextBrush;
            Resources["CardHoverBrush"] = SystemColors.ControlBrush;
            Resources["CardHoverBorderBrush"] = SystemColors.HighlightBrush;
            Resources["AccentSurfaceBrush"] = SystemColors.HighlightBrush;
            Resources["AccentFillBrush"] = SystemColors.HighlightBrush;
            Resources["GreenSurfaceBrush"] = SystemColors.ControlBrush;
            Resources["WarmBrush"] = SystemColors.HighlightBrush;
            Resources["WarmSurfaceBrush"] = SystemColors.ControlBrush;
            ShellShape.Fill = SystemColors.WindowBrush;
            Resources["PanelFadeBrush"] = FadeBrush(SystemColors.WindowColor);
            ShellShape.Effect = null;
            WebOverlay.Visibility = Visibility.Collapsed;
            IdleHiddenTab.Background = SystemColors.GrayTextBrush;
            RefreshSpiderBuddy();

            foreach (var bar in _waveformBars)
            {
                bar.Background = (Brush)Resources["WaveBrush"];
            }

            return;
        }

        var primary = MakeBrush(dark ? "#FFFFFFFF" : "#FF0B0B0D");
        var secondary = MakeBrush(dark ? "#FF9C9CA3" : "#FF5A5A63");
        var chip = MakeBrush(dark ? "#FF2C2C2E" : "#FFE7E7EC");
        var track = MakeBrush(dark ? "#FF3A3A3C" : "#FFD0D0D8");
        var accent = MakeBrush(accentColor);
        var accentColorValue = (Color)ColorConverter.ConvertFromString(accentColor);
        var green = MakeBrush(dark ? "#FF30D158" : "#FF146B30");

        Resources["PrimaryBrush"] = primary;
        Resources["SecondaryBrush"] = secondary;
        Resources["ChipBrush"] = chip;
        Resources["TrackBrush"] = track;
        Resources["BlueBrush"] = accent;
        Resources["GreenBrush"] = green;
        Resources["AccentInkBrush"] = MakeBrush("#FF0B0B0D");
        Resources["AccentTextBrush"] = dark
            ? accent
            : MakeBrush(Darken(accentColorValue, 0.42).ToString());
        Resources["GreenInkBrush"] = MakeBrush(dark ? "#FF0B0B0D" : "#FFFFFFFF");
        Resources["HoverBrush"] = MakeBrush(dark ? "#14FFFFFF" : "#0F000000");
        Resources["PressedBrush"] = MakeBrush(dark ? "#24FFFFFF" : "#1C000000");
        Resources["TextHoverBrush"] = primary;
        Resources["CircleBorderBrush"] = MakeBrush(dark ? "#00000000" : "#2E000000");
        Resources["ShellBorderBrush"] = spider
            ? SpiderSkin.Rim(dark)
            : MakeBrush(dark ? "#17FFFFFF" : "#4D000000");
        Resources["WaveBrush"] = new LinearGradientBrush(
            accentColorValue,
            (Color)ColorConverter.ConvertFromString(spider ? SpiderSkin.Blue : "#FFFF7A45"),
            new Point(0, 1),
            new Point(1, 0));

        Resources["HomeTileBrush"] = MakeBrush(dark ? "#0CFFFFFF" : "#0A000000");
        Resources["HairlineBrush"] = MakeBrush(dark ? "#1AFFFFFF" : "#1A000000");
        Resources["CardBrush"] = dark
            ? VerticalGradient("#1AFFFFFF", "#0CFFFFFF")
            : VerticalGradient("#FFFFFFFF", "#F6F6FA");
        Resources["CardBorderBrush"] = MakeBrush(dark ? "#1FFFFFFF" : "#12000000");
        Resources["CardHoverBrush"] = dark
            ? VerticalGradient("#26FFFFFF", "#16FFFFFF")
            : VerticalGradient("#FFFFFF", "#EDEDF3");
        Resources["CardHoverBorderBrush"] = MakeBrush(dark ? "#38FFFFFF" : "#24000000");
        Resources["AccentSurfaceBrush"] =
            AlphaBrush(accentColorValue, dark ? (byte)0x24 : (byte)0x16);
        Resources["AccentFillBrush"] = accent;
        Resources["GreenSurfaceBrush"] = AlphaBrush(green.Color, dark ? (byte)0x24 : (byte)0x1A);
        var warmColor = dark
            ? Color.FromRgb(0xFF, 0xB3, 0x40)
            : Color.FromRgb(0x9A, 0x5B, 0x00);
        Resources["WarmBrush"] = MakeBrush(dark ? "#FFFFB340" : "#FF9A5B00");
        Resources["WarmSurfaceBrush"] = AlphaBrush(warmColor, dark ? (byte)0x24 : (byte)0x1A);

        ShellShape.Fill = spider ? SpiderSkin.ShellFill(dark) : MakeBrush(dark ? "#FF000000" : "#FFF8F8FB");
        Resources["PanelFadeBrush"] = FadeBrush(
            spider ? SpiderSkin.ShellBase(dark) : ((SolidColorBrush)ShellShape.Fill).Color);
        ShellShape.Effect = dark
            ? null
            : new DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 22,
                ShadowDepth = 7,
                Direction = 270,
                Opacity = 0.18,
                RenderingBias = RenderingBias.Performance
            };
        IdleHiddenTab.Background = spider
            ? SpiderSkin.HiddenTab(dark)
            : HiddenTabGradient(dark);
        WebOverlay.Stroke = SpiderSkin.WebStroke(dark);
        WebOverlay.Visibility = spider ? Visibility.Visible : Visibility.Collapsed;
        RefreshSpiderBuddy();

        foreach (var bar in _waveformBars)
        {
            bar.Background = (Brush)Resources["WaveBrush"];
        }
    }

    private static Brush HiddenTabGradient(bool dark)
    {
        var rgb = dark ? Colors.White : Colors.Black;
        var fade = Color.FromArgb(0x00, rgb.R, rgb.G, rgb.B);
        var core = Color.FromArgb(dark ? (byte)0xB3 : (byte)0x8C, rgb.R, rgb.G, rgb.B);

        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 0)
        };
        brush.GradientStops.Add(new GradientStop(fade, 0));
        brush.GradientStops.Add(new GradientStop(core, 0.5));
        brush.GradientStops.Add(new GradientStop(fade, 1));
        brush.Freeze();
        return brush;
    }

    private static Color Darken(Color color, double amount)
    {
        var factor = 1 - Math.Clamp(amount, 0, 1);
        return Color.FromArgb(
            color.A,
            (byte)Math.Round(color.R * factor),
            (byte)Math.Round(color.G * factor),
            (byte)Math.Round(color.B * factor));
    }

    private static SolidColorBrush MakeBrush(string value)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
        brush.Freeze();
        return brush;
    }

    private static SolidColorBrush AlphaBrush(Color color, byte alpha)
    {
        var brush = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }

    private static LinearGradientBrush FadeBrush(Color color)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1)
        };
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 0));
        brush.GradientStops.Add(new GradientStop(color, 1));
        brush.Freeze();
        return brush;
    }

    private static LinearGradientBrush VerticalGradient(string top, string bottom)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1)
        };
        brush.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(top), 0));
        brush.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(bottom), 1));
        brush.Freeze();
        return brush;
    }

    private static bool IsSystemLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value != 0;
        }
        catch
        {
            return false;
        }
    }

    private void RegisterConfiguredHotkey()
    {
        if (_windowHandle == IntPtr.Zero)
        {
            return;
        }

        if (_hotkeyRegistered)
        {
            NativeMethods.UnregisterHotKey(_windowHandle, HotKeyId);
            _hotkeyRegistered = false;
        }

        var (modifiers, virtualKey) = ParseHotkey(_settings.Hotkey);
        _hotkeyRegistered = NativeMethods.RegisterHotKey(_windowHandle, HotKeyId, modifiers, virtualKey);
    }

    private static (uint Modifiers, uint VirtualKey) ParseHotkey(string hotkey)
    {
        uint modifiers = 0;
        uint key = 0x49;

        foreach (var part in hotkey.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    modifiers |= NativeMethods.MOD_CONTROL;
                    break;
                case "alt":
                    modifiers |= NativeMethods.MOD_ALT;
                    break;
                case "shift":
                    modifiers |= NativeMethods.MOD_SHIFT;
                    break;
                case "space":
                    key = 0x20;
                    break;
                default:
                    if (part.Length == 1)
                    {
                        key = char.ToUpperInvariant(part[0]);
                    }
                    break;
            }
        }

        return (modifiers | NativeMethods.MOD_NOREPEAT, key);
    }

    // --------------------------------------------------------------- hit-testing

    // Only the black shell is interactive; the rest of the host window is
    // transparent to the mouse.
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt32() == HotKeyId)
        {
            handled = true;
            ToggleIsland();
            return IntPtr.Zero;
        }

        if (msg != WM_NCHITTEST)
        {
            return IntPtr.Zero;
        }

        if (!NativeMethods.GetWindowRect(hwnd, out var windowRect))
        {
            return IntPtr.Zero;
        }

        var raw = lParam.ToInt64();
        var screenX = (short)(raw & 0xFFFF);
        var screenY = (short)((raw >> 16) & 0xFFFF);

        var shellWidthDevice = CurrentShellWidth * _dpiScale;
        var shellHeightDevice = CurrentShellHeight * _dpiScale;
        var windowWidthDevice = (double)(windowRect.Right - windowRect.Left);

        var shellLeft = windowRect.Left + (windowWidthDevice - shellWidthDevice) / 2;

        var relativeX = (screenX - shellLeft) / _dpiScale;
        var relativeY = (screenY - windowRect.Top) / _dpiScale;

        var inside =
            relativeX >= 0 && relativeX <= CurrentShellWidth &&
            relativeY >= 0 && relativeY <= CurrentShellHeight;

        // The ears leave transparent notches at the top corners; those must stay
        // click-through, so test against the actual black silhouette.
        if (inside && ShellShape.Data is { } shape)
        {
            inside = shape.FillContains(new Point(relativeX, relativeY));
        }

        handled = true;
        return new IntPtr(inside ? HTCLIENT : HTTRANSPARENT);
    }

    private double CurrentShellWidth => (double)Shell.GetValue(WidthProperty);

    private double CurrentShellHeight => (double)Shell.GetValue(HeightProperty);

    // ------------------------------------------------------------- notch shape

    private void Shell_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateShellShape();

    /// <summary>
    /// Rebuilds the single black silhouette: concave quarter-circle ears at the
    /// top left/right (the fillets that melt into the screen edge) plus the
    /// rounded bottom of the body.
    /// </summary>
    private void UpdateShellShape()
    {
        var width = Shell.ActualWidth > 0 ? Shell.ActualWidth : Shell.Width;
        var height = Shell.ActualHeight > 0 ? Shell.ActualHeight : Shell.Height;

        if (width <= 0 || height <= 0)
        {
            return;
        }

        var scale = _preview?.Scale ?? _settings.Scale;
        var ear = EarRadius * scale;
        var bottom = Math.Max(0, (_preview?.CornerRadius ?? _settings.CornerRadius) * scale);
        var positionMode = _preview?.PositionMode ?? _settings.PositionMode;

        ContentScale.ScaleX = scale;
        ContentScale.ScaleY = scale;
        States.Margin = new Thickness(20 * scale, 0, 20 * scale, 0);

        ShellShape.Data = positionMode == PositionMode.FloatingPill
            ? BuildPillGeometry(width, height, Math.Min(24 * scale, height / 2))
            : BuildNotchGeometry(width, height, ear, bottom);

        WebOverlay.Clip = ShellShape.Data;
        if (WebOverlay.Visibility == Visibility.Visible)
        {
            WebOverlay.Data = SpiderSkin.BuildWeb(width, height);
        }

        WebOverlay.Opacity = height < 60 ? 0.55 : 1;
    }

    private static Geometry BuildPillGeometry(double width, double height, double radius)
    {
        var r = Math.Min(radius, Math.Min(width / 2, height / 2));
        var figure = new PathFigure
        {
            StartPoint = new Point(r, 0),
            IsClosed = true,
            IsFilled = true
        };

        figure.Segments.Add(new LineSegment(new Point(width - r, 0), true));
        figure.Segments.Add(new ArcSegment(new Point(width, r), new Size(r, r), 0, false,
            SweepDirection.Clockwise, true));
        figure.Segments.Add(new LineSegment(new Point(width, height - r), true));
        figure.Segments.Add(new ArcSegment(new Point(width - r, height), new Size(r, r), 0, false,
            SweepDirection.Clockwise, true));
        figure.Segments.Add(new LineSegment(new Point(r, height), true));
        figure.Segments.Add(new ArcSegment(new Point(0, height - r), new Size(r, r), 0, false,
            SweepDirection.Clockwise, true));
        figure.Segments.Add(new LineSegment(new Point(0, r), true));
        figure.Segments.Add(new ArcSegment(new Point(r, 0), new Size(r, r), 0, false,
            SweepDirection.Clockwise, true));

        var geometry = new PathGeometry(new[] { figure });
        geometry.Freeze();
        return geometry;
    }

    private static Geometry BuildNotchGeometry(double width, double height, double topRadius, double bottomRadius)
    {
        var r = Math.Min(topRadius, Math.Min(width / 2, height));
        var b = Math.Min(bottomRadius, Math.Min((width - 2 * r) / 2, height - r));
        b = Math.Max(0, b);

        var figure = new PathFigure
        {
            StartPoint = new Point(0, 0),
            IsClosed = true,
            IsFilled = true
        };

        // Top-left ear: concave quarter-circle from the screen edge into the body.
        figure.Segments.Add(new ArcSegment(
            new Point(r, r), new Size(r, r), 0, false, SweepDirection.Clockwise, true));

        // Left side down to the bottom-left corner.
        if (b > 0.001)
        {
            figure.Segments.Add(new LineSegment(new Point(r, height - b), true));
            figure.Segments.Add(new ArcSegment(
                new Point(r + b, height), new Size(b, b), 0, false, SweepDirection.Counterclockwise, true));
        }
        else
        {
            figure.Segments.Add(new LineSegment(new Point(r, height), true));
        }

        // Bottom edge, bottom-right corner, right side.
        if (b > 0.001)
        {
            figure.Segments.Add(new LineSegment(new Point(width - r - b, height), true));
            figure.Segments.Add(new ArcSegment(
                new Point(width - r, height - b), new Size(b, b), 0, false, SweepDirection.Counterclockwise, true));
        }
        else
        {
            figure.Segments.Add(new LineSegment(new Point(width - r, height), true));
        }

        figure.Segments.Add(new LineSegment(new Point(width - r, r), true));

        // Top-right ear, mirroring the left one.
        figure.Segments.Add(new ArcSegment(
            new Point(width, 0), new Size(r, r), 0, false, SweepDirection.Clockwise, true));

        var geometry = new PathGeometry(new[] { figure });
        geometry.Freeze();
        return geometry;
    }

    // ------------------------------------------------------------- state machine

    private (double Width, double Height) SizeFor(IslandState state)
    {
        var (width, height) = state switch
        {
            IslandState.Idle => (IdleWidth, IdleHeight),
            IslandState.IdleHidden => (IdleHiddenWidth, IdleHiddenHeight),
            IslandState.IdleExpanded => (IdleExpandedWidth, IdleExpandedHeight),
            IslandState.CompactMedia => (CompactMediaWidth, CompactMediaHeight),
            IslandState.ExpandedMedia => (ExpandedMediaWidth, ExpandedMediaHeight),
            IslandState.CompactFocus => (CompactFocusWidth, CompactFocusHeight),
            IslandState.FocusPicker => (FocusPickerWidth, FocusPickerHeight),
            IslandState.FocusTimer => (TimerWidth, TimerHeight),
            IslandState.FocusComplete => (FocusCompleteWidth, FocusCompleteHeight),
            IslandState.Notification => (NotificationWidth, NotificationHeight),
            IslandState.Charging => (ChargingWidth, ChargingHeight),
            IslandState.Volume => (VolumeWidth, VolumeHeight),
            IslandState.Brightness => (BrightnessWidth, BrightnessHeight),
            IslandState.Shelf => (ShelfWidth, ShelfHeight),
            IslandState.Notes => (NotesWidth, NotesHeight),
            IslandState.Calendar => (CalendarWidth, CalendarHeight),
            IslandState.Prompt => (PromptWidth, PromptHeight),
            IslandState.Mirror => (MirrorWidth, MirrorHeight),
            IslandState.LiveActivity => (LiveActivityWidth, LiveActivityHeight),
            _ => (IdleWidth, IdleHeight)
        };

        return (width * (_preview?.Scale ?? _settings.Scale), height * (_preview?.Scale ?? _settings.Scale));
    }

    private FrameworkElement PanelFor(IslandState state) => state switch
    {
        IslandState.Idle => IdlePanel,
        IslandState.IdleHidden => IdleHiddenPanel,
        IslandState.IdleExpanded => IdleExpandedPanel,
        IslandState.CompactMedia => CompactPanel,
        IslandState.ExpandedMedia => ExpandedPanel,
        IslandState.CompactFocus => CompactFocusPanel,
        IslandState.FocusPicker => FocusPickerPanel,
        IslandState.FocusTimer => TimerPanel,
        IslandState.FocusComplete => FocusCompletePanel,
        IslandState.Notification => NotificationPanel,
        IslandState.Charging => ChargingPanel,
        IslandState.Volume => VolumePanel,
        IslandState.Brightness => BrightnessPanel,
        IslandState.Shelf => ShelfPanel,
        IslandState.Notes => NotesPanel,
        IslandState.Calendar => CalendarPanel,
        IslandState.Prompt => PromptPanel,
        IslandState.Mirror => MirrorPanel,
        IslandState.LiveActivity => LiveActivityPanel,
        _ => IdlePanel
    };

    private void ShowPanel(IslandState state)
    {
        IdlePanel.Visibility = state == IslandState.Idle ? Visibility.Visible : Visibility.Collapsed;
        IdleHiddenPanel.Visibility = state == IslandState.IdleHidden ? Visibility.Visible : Visibility.Collapsed;
        IdleExpandedPanel.Visibility = state == IslandState.IdleExpanded ? Visibility.Visible : Visibility.Collapsed;
        CompactPanel.Visibility = state == IslandState.CompactMedia ? Visibility.Visible : Visibility.Collapsed;
        ExpandedPanel.Visibility = state == IslandState.ExpandedMedia ? Visibility.Visible : Visibility.Collapsed;
        CompactFocusPanel.Visibility = state == IslandState.CompactFocus ? Visibility.Visible : Visibility.Collapsed;
        FocusPickerPanel.Visibility = state == IslandState.FocusPicker ? Visibility.Visible : Visibility.Collapsed;
        TimerPanel.Visibility = state == IslandState.FocusTimer ? Visibility.Visible : Visibility.Collapsed;
        FocusCompletePanel.Visibility = state == IslandState.FocusComplete ? Visibility.Visible : Visibility.Collapsed;
        NotificationPanel.Visibility = state == IslandState.Notification ? Visibility.Visible : Visibility.Collapsed;
        ChargingPanel.Visibility = state == IslandState.Charging ? Visibility.Visible : Visibility.Collapsed;
        VolumePanel.Visibility = state == IslandState.Volume ? Visibility.Visible : Visibility.Collapsed;
        BrightnessPanel.Visibility = state == IslandState.Brightness ? Visibility.Visible : Visibility.Collapsed;
        ShelfPanel.Visibility = state == IslandState.Shelf ? Visibility.Visible : Visibility.Collapsed;
        NotesPanel.Visibility = state == IslandState.Notes ? Visibility.Visible : Visibility.Collapsed;
        CalendarPanel.Visibility = state == IslandState.Calendar ? Visibility.Visible : Visibility.Collapsed;
        PromptPanel.Visibility = state == IslandState.Prompt ? Visibility.Visible : Visibility.Collapsed;
        MirrorPanel.Visibility = state == IslandState.Mirror ? Visibility.Visible : Visibility.Collapsed;
        LiveActivityPanel.Visibility = state == IslandState.LiveActivity ? Visibility.Visible : Visibility.Collapsed;

        SyncMirror();
    }

    /// <summary>
    /// Morphs the shell from whatever shape it is in right now into the next
    /// state's shape — one continuous motion of the same island, no collapse
    /// step, no window resizing.
    /// </summary>
    private void Transition(IslandState next)
    {
        if (next == _state)
        {
            RenderState();
            return;
        }

        var previous = _state;
        _state = next;

        // Leaving the prompt panel ends the request; a late answer must not
        // arrive for a panel the user already closed. Overlays keep it alive
        // because they restore the panel they interrupted.
        if (previous == IslandState.Prompt && next != IslandState.Prompt && !IsOverlay(next))
        {
            CancelPrompt();
        }

        // Navigating away from a toast dismisses the whole peek session. Without
        // this, the countdown would keep dequeuing cards nobody can see.
        if (previous == IslandState.Notification && next != IslandState.Notification)
        {
            ClearNotificationSession();
        }

        // A pin only belongs to an expanded panel. Any return to a resting state
        // closes the pin and removes the outside-click hook.
        if (next is IslandState.Idle or IslandState.IdleHidden
            or IslandState.CompactMedia or IslandState.CompactFocus)
        {
            SetPinned(false);
        }

        if (next == IslandState.Idle || next == IslandState.IdleHidden)
        {
            _lastPresence = DateTime.UtcNow;
        }

        _hoverLeave.Stop();

        // A real crossfade: the outgoing panel stays visible underneath while it
        // fades out, so the shell never flashes an empty frame between states.
        var previousPanel = PanelFor(previous);
        var previousVisible = previousPanel.Visibility == Visibility.Visible;
        var previousOpacity = previousVisible ? previousPanel.Opacity : 0;

        var nextPanel = PanelFor(next);
        ShowPanel(next);
        nextPanel.BeginAnimation(OpacityProperty, null);
        nextPanel.IsHitTestVisible = true;

        if (previousPanel != nextPanel)
        {
            if (previousVisible && previousOpacity > 0.01)
            {
                previousPanel.Visibility = Visibility.Visible;
                FadeOutPanel(previousPanel, previousOpacity);
            }
            else
            {
                previousPanel.BeginAnimation(OpacityProperty, null);
                previousPanel.Opacity = 1;
            }
        }

        // No HeldEnd clock may survive the fade: a clock that stays in the timing
        // tree keeps WPF's render thread ticking while the island is idle. Base
        // value 1 + FillBehavior.Stop + explicit cleanup on Completed.
        nextPanel.Opacity = 1;
        var fade = new DoubleAnimation(0, 1, ContentFade)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        };
        fade.Completed += (_, _) => nextPanel.BeginAnimation(OpacityProperty, null);
        nextPanel.BeginAnimation(OpacityProperty, fade);

        var (width, height) = SizeFor(next);
        var growing = height > CurrentShellHeight + 0.5;

        MorphShell(width, height, growing, growing ? SpringEase() : EaseOut());

        var buddyWasVisible = SpiderBuddy.Visibility == Visibility.Visible;
        RenderState();
        if (!buddyWasVisible && SpiderBuddy.Visibility == Visibility.Visible)
        {
            PlaySpiderBuddySwing();
        }

        if (next == IslandState.IdleExpanded && previous != IslandState.IdleExpanded)
        {
            PlayIdleHomeEntrance();
        }
        else if (IsOverlay(next) && !ReducedMotion)
        {
            SlideIn(nextPanel, -10, 240, 0);
        }
    }

    /// <summary>
    /// Fades a panel that just lost the foreground. It is re-shown because
    /// <see cref="ShowPanel"/> collapsed it already; both panels share the same
    /// grid slot, so the two opacity ramps read as one crossfade.
    /// </summary>
    private void FadeOutPanel(FrameworkElement panel, double from)
    {
        panel.BeginAnimation(OpacityProperty, null);
        panel.Opacity = from;
        panel.IsHitTestVisible = false;

        var fade = new DoubleAnimation(from, 0, ContentFade)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        };

        fade.Completed += (_, _) =>
        {
            panel.IsHitTestVisible = true;

            // A rapid second transition may have brought this panel back; its
            // own fade-in owns the opacity now, so only hide it when it is still
            // not the active panel.
            if (PanelFor(_state) == panel)
            {
                return;
            }

            panel.BeginAnimation(OpacityProperty, null);
            panel.Opacity = 1;
            panel.Visibility = Visibility.Collapsed;
        };

        panel.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>
    /// Reduce motion only softens movement; the hover grace stays at its normal
    /// length because it is a behavioural pause, not an animation.
    /// </summary>
    private bool ReducedMotion => _preview?.ReducedMotion ?? _settings.ReducedMotion;

    private TimeSpan ContentFade
        => ReducedMotion ? TimeSpan.FromMilliseconds(1) : NormalContentFade;

    private TimeSpan MorphDuration(bool growing)
        => ReducedMotion
            ? TimeSpan.FromMilliseconds(1)
            : growing ? NormalExpandDuration : NormalCollapseDuration;

    private static IEasingFunction SpringEase()
        => new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.14 };

    private static IEasingFunction EaseOut()
        => new CubicEase { EasingMode = EasingMode.EaseOut };

    private void MorphShell(double width, double height, bool growing, IEasingFunction easing)
    {
        // Read the effective (animated) size before the base value moves, so the
        // next morph starts from what is on screen rather than from the target.
        var fromWidth = Shell.ActualWidth > 0 ? Shell.ActualWidth : width;
        var fromHeight = Shell.ActualHeight > 0 ? Shell.ActualHeight : height;

        // Base values are the destination, so the animation can be released the
        // moment it finishes instead of leaving a HeldEnd clock running.
        Shell.Width = width;
        Shell.Height = height;

        var duration = MorphDuration(growing);
        var widthAnimation = new DoubleAnimation(fromWidth, width, duration)
        {
            EasingFunction = easing,
            FillBehavior = FillBehavior.Stop
        };
        var heightAnimation = new DoubleAnimation(fromHeight, height, duration)
        {
            EasingFunction = easing,
            FillBehavior = FillBehavior.Stop
        };

        widthAnimation.Completed += (_, _) => Shell.BeginAnimation(WidthProperty, null);
        heightAnimation.Completed += (_, _) => Shell.BeginAnimation(HeightProperty, null);

        Shell.BeginAnimation(WidthProperty, widthAnimation, HandoffBehavior.SnapshotAndReplace);
        Shell.BeginAnimation(HeightProperty, heightAnimation, HandoffBehavior.SnapshotAndReplace);
    }

    /// <summary>
    /// The home panel arrives in three small steps so the eye reads time first,
    /// then controls. Only a transform moves; the panel-level fade owns opacity.
    /// </summary>
    private void PlayIdleHomeEntrance()
    {
        if (ReducedMotion)
        {
            return;
        }

        SlideIn(IdleHomeHero, -9, 280, 0);
        SlideIn(IdleHomeHeader, -8, 270, 40);
        SlideIn(IdleHomeDock, -6, 240, 70);
    }

    private static void SlideIn(FrameworkElement element, double fromY, double durationMs, int delayMs)
    {
        if (element.RenderTransform is not TranslateTransform transform)
        {
            transform = new TranslateTransform();
            element.RenderTransform = transform;
        }

        transform.BeginAnimation(TranslateTransform.YProperty, null);
        transform.Y = fromY;

        var animation = new DoubleAnimation(fromY, 0, TimeSpan.FromMilliseconds(durationMs))
        {
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        };

        // The base value holds the start offset until the animation begins, then
        // parks at zero the frame it lands so no clock survives the entrance.
        animation.Completed += (_, _) =>
        {
            transform.BeginAnimation(TranslateTransform.YProperty, null);
            transform.Y = 0;
        };
        transform.BeginAnimation(TranslateTransform.YProperty, animation);
    }

    // ---------------------------------------------------------- spider mascot

    /// <summary>
    /// The hanging mascot belongs to the Spider skin and to the ambient states
    /// only: transient HUDs, panels and focus keep a clean silhouette.
    /// </summary>
    private static bool IsSpiderBuddyState(IslandState state)
        => state is IslandState.Idle or IslandState.IdleExpanded
            or IslandState.CompactMedia or IslandState.ExpandedMedia;

    private void RefreshSpiderBuddy()
    {
        ShowSpiderBuddy(IsSpiderBuddyState(_state));
        if (SpiderBuddy.Visibility != Visibility.Visible)
        {
            SpiderSway.BeginAnimation(RotateTransform.AngleProperty, null);
            SpiderSway.Angle = 0;
        }
    }

    /// <summary>
    /// Visibility only; the offscreen render harness uses this to place the
    /// mascot without running the island state machine.
    /// </summary>
    internal void ShowSpiderBuddy(bool visible)
    {
        var skin = _preview?.Skin ?? _settings.Skin;
        SpiderBuddy.Visibility = visible && skin == SkinMode.Spider && !_highContrast
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void PlaySpiderBuddySwing()
    {
        if (SpiderBuddy.Visibility != Visibility.Visible)
        {
            return;
        }

        SpiderSway.BeginAnimation(RotateTransform.AngleProperty, null);
        SpiderSway.Angle = 0;

        if (ReducedMotion)
        {
            return;
        }

        // One damped swing on appearance, then the mascot rests. A forever
        // clock in the timing tree would keep the idle island rendering.
        var swing = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromMilliseconds(1500),
            FillBehavior = FillBehavior.Stop
        };
        swing.KeyFrames.Add(new LinearDoubleKeyFrame(0, TimeSpan.Zero));
        swing.KeyFrames.Add(new EasingDoubleKeyFrame(8.5, TimeSpan.FromMilliseconds(230))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
        swing.KeyFrames.Add(new EasingDoubleKeyFrame(-5.5, TimeSpan.FromMilliseconds(640))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
        });
        swing.KeyFrames.Add(new EasingDoubleKeyFrame(3.2, TimeSpan.FromMilliseconds(1010))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
        });
        swing.KeyFrames.Add(new EasingDoubleKeyFrame(-1.4, TimeSpan.FromMilliseconds(1310))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
        });
        swing.KeyFrames.Add(new EasingDoubleKeyFrame(0, TimeSpan.FromMilliseconds(1500))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
        });
        SpiderSway.BeginAnimation(RotateTransform.AngleProperty, swing);
    }

    // ------------------------------------------------------------ data rendering

    private void RenderState()
    {
        // The 500 ms progress tick only exists while the player is open.
        var wantsTimeline = _state == IslandState.ExpandedMedia;
        if (wantsTimeline != _timeline.IsEnabled)
        {
            if (wantsTimeline)
            {
                _timeline.Start();
            }
            else
            {
                _timeline.Stop();
            }
        }

        switch (_state)
        {
            case IslandState.Idle:
                StopWaveform();
                UpdateClock();
                break;

            case IslandState.IdleHidden:
                StopWaveform();
                break;

            case IslandState.IdleExpanded:
                StopWaveform();
                UpdateClock();
                BuildQuickLaunch();
                break;

            case IslandState.CompactMedia:
                RenderCompactMedia();
                break;

            case IslandState.ExpandedMedia:
                StopWaveform();
                RenderExpandedMedia();
                break;

            case IslandState.CompactFocus:
                StopWaveform();
                UpdateTimerVisual();
                break;

            case IslandState.FocusPicker:
                StopWaveform();
                break;

            case IslandState.FocusTimer:
                StopWaveform();
                UpdateTimerVisual();
                break;

            case IslandState.FocusComplete:
                StopWaveform();
                RenderFocusComplete();
                break;

            case IslandState.Notification:
                StopWaveform();
                RenderNotificationPanel();
                break;

            case IslandState.Charging:
                StopWaveform();
                RenderChargingPanel();
                break;

            case IslandState.Volume:
                StopWaveform();
                RenderVolumePanel();
                break;

            case IslandState.Brightness:
                StopWaveform();
                RenderBrightnessPanel();
                break;

            case IslandState.Shelf:
                StopWaveform();
                BuildShelfItems();
                break;

            case IslandState.Notes:
                StopWaveform();
                BuildNotesItems();
                break;

            case IslandState.Calendar:
                StopWaveform();
                RenderCalendarPanel();
                break;

            case IslandState.Prompt:
                StopWaveform();
                break;

            case IslandState.Mirror:
                StopWaveform();
                RenderMirrorPanel();
                break;

            case IslandState.LiveActivity:
                StopWaveform();
                RenderLiveActivityPanel();
                break;
        }

        SyncCapture();
        RefreshSpiderBuddy();
    }

    private void RenderCompactMedia()
    {
        var info = _currentMedia;
        SetArtwork(CompactArt, info?.Thumbnail);
    }

    /// <summary>
    /// WASAPI capture follows the visible waveform, not just playback: an open
    /// panel, an overlay, the full player or a hidden island all stop the
    /// loopback session until the compact bars are actually on screen again.
    /// </summary>
    private void SyncCapture()
    {
        var want = !_paused
            && _settings.MediaEnabled
            && _state == IslandState.CompactMedia
            && Visibility == Visibility.Visible
            && _currentMedia?.IsPlaying == true;

        EnsureCapture(want);

        if (want)
        {
            if (!_waveform.IsEnabled)
            {
                _waveformResting = false;
                _waveform.Start();
            }
        }
        else if (_waveform.IsEnabled)
        {
            StopWaveform();
        }
    }

    /// <summary>
    /// The shared WASAPI capture is owned by the primary window only. Every
    /// monitor reads the same levels, so a second capture would be wasted work.
    /// </summary>
    private void EnsureCapture(bool want)
    {
        // Every shell claims the shared capture lease while its own waveform is
        // visible, so one monitor collapsing cannot freeze another's bars.
        var shouldCapture = want && !_paused && _settings.MediaEnabled;
        if (shouldCapture == _captureWanted)
        {
            return;
        }

        _captureWanted = shouldCapture;
        if (shouldCapture)
        {
            _visualizer.Start();
        }
        else
        {
            _visualizer.Stop();
        }
    }

    private void RenderExpandedMedia()
    {
        var info = _currentMedia;
        ExpandedTitle.Text = string.IsNullOrWhiteSpace(info?.Title) ? "No media" : info!.Title;
        ExpandedArtist.Text = info?.Artist ?? string.Empty;
        SetArtwork(ExpandedArt, info?.Thumbnail);
        PlayPauseGlyph.Text = info?.IsPlaying == true ? "\uE769" : "\uE768";
        UpdateMediaModeButtons();

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(UpdateProgressVisual));
    }

    private void UpdateMediaModeButtons()
    {
        var shuffle = _media.IsShuffleEnabled;
        ShuffleButton.Opacity = shuffle ? 1 : 0.55;
        ShuffleButton.Foreground = (Brush)FindResource(shuffle ? "BlueBrush" : "PrimaryBrush");

        var repeat = _media.RepeatMode;
        RepeatButton.Content = repeat == MediaPlaybackAutoRepeatMode.Track ? "\uE8ED" : "\uE8EE";
        RepeatButton.Opacity = repeat == MediaPlaybackAutoRepeatMode.None ? 0.55 : 1;
        RepeatButton.Foreground = (Brush)FindResource(repeat == MediaPlaybackAutoRepeatMode.None
            ? "PrimaryBrush"
            : "BlueBrush");
    }

    private void StopWaveform()
    {
        // SyncCapture owns the WASAPI session, so overlay states only need to
        // rest the bars — and only once, since RenderState runs per event.
        _waveform.Stop();
        if (_waveformResting)
        {
            return;
        }

        _waveformResting = true;
        foreach (var bar in _waveformBars)
        {
            bar.BeginAnimation(UIElement.RenderTransformProperty, null);
            if (bar.RenderTransform is ScaleTransform scale)
            {
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                scale.ScaleY = WaveformFloor;
            }
        }
    }

    /// <summary>
    /// Decodes each track once and shares the frozen bitmap between the compact
    /// and expanded artwork. Playback events fire far more often than the cover
    /// changes, and 1024 px decodes were both slow and memory-hungry for a
    /// player whose largest cover slot is 104 DIP.
    /// </summary>
    private void SetArtwork(System.Windows.Controls.Image image, byte[]? thumbnail)
    {
        BitmapSource? source = null;

        if (thumbnail is { Length: > 0 })
        {
            if (!ReferenceEquals(thumbnail, _artworkBytes) || _artworkSource is null)
            {
                try
                {
                    _artworkSource = CreateThumbnail(thumbnail);
                    _artworkBytes = thumbnail;
                }
                catch
                {
                    _artworkSource = null;
                    _artworkBytes = thumbnail;
                }
            }

            source = (BitmapSource?)_artworkSource;
        }
        else
        {
            _artworkBytes = null;
            _artworkSource = null;
        }

        if (!ReferenceEquals(image.Source, source))
        {
            image.Source = source;
        }
    }

    private void MirrorPreview_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Border.ClipToBounds only clips to the outer rectangle; the camera
        // image needs its own rounded clip to match the card corner radius.
        if (sender is System.Windows.Controls.Image { ActualWidth: > 0, ActualHeight: > 0 } image)
        {
            image.Clip = new RectangleGeometry(
                new Rect(0, 0, image.ActualWidth, image.ActualHeight), 24, 24);
        }
    }

    private void Artwork_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Border.ClipToBounds only clips to the outer rectangle; the artwork needs
        // its own rounded clip to match the tile's corner radius.
        if (sender is System.Windows.Controls.Image { ActualWidth: > 0, ActualHeight: > 0 } image)
        {
            var radius = image == ExpandedArt ? 21.0 : 10.0;
            image.Clip = new RectangleGeometry(
                new Rect(0, 0, image.ActualWidth, image.ActualHeight), radius, radius);
        }
    }

    private static BitmapImage? CreateThumbnail(byte[]? data)
    {
        if (data is null || data.Length == 0)
        {
            return null;
        }

        var bitmap = new BitmapImage();
        using var stream = new MemoryStream(data);
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.DecodePixelWidth = 256;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private void UpdateProgressVisual()
    {
        if (_state != IslandState.ExpandedMedia || _seeking)
        {
            return;
        }

        var info = _currentMedia;
        PlayPauseGlyph.Text = info?.IsPlaying == true ? "\uE769" : "\uE768";
        PositionText.Text = SystemStatusService.FormatDuration(_position);

        // Live streams and some sessions report no duration; "0:00" would be
        // misleading, so show an unknown duration instead.
        DurationText.Text = _duration > TimeSpan.Zero
            ? SystemStatusService.FormatDuration(_duration)
            : "--:--";

        ProgressHit.Maximum = Math.Max(0, _duration.TotalSeconds);
        ProgressHit.Value = Math.Clamp(_position.TotalSeconds, 0, ProgressHit.Maximum);

        var track = ProgressTrack.ActualWidth;
        if (track <= 0)
        {
            return;
        }

        var ratio = _duration > TimeSpan.Zero
            ? Math.Clamp(_position.TotalMilliseconds / _duration.TotalMilliseconds, 0, 1)
            : 0;
        ProgressFill.Width = track * ratio;
        ProgressKnob.Visibility = _duration > TimeSpan.Zero ? Visibility.Visible : Visibility.Collapsed;
        PositionKnob(track, ratio);
    }

    private void PositionKnob(double trackWidth, double ratio)
    {
        var travel = Math.Max(0, trackWidth - ProgressKnob.Width);
        ProgressKnobTransform.X = Math.Clamp(
            trackWidth * ratio - ProgressKnob.Width / 2, 0, travel);
    }

    // ------------------------------------------------------------------- timer

    private void UpdateTimerVisual()
    {
        var stopwatch = _focusTimer.IsStopwatch && _focusTimer.Target <= TimeSpan.Zero;
        var remaining = _focusTimer.Target > TimeSpan.Zero
            ? _focusTimer.Target - _focusTimer.Elapsed
            : TimeSpan.Zero;
        if (remaining < TimeSpan.Zero)
        {
            remaining = TimeSpan.Zero;
        }

        var text = stopwatch
            ? SystemStatusService.FormatDuration(_focusTimer.Elapsed)
            : SystemStatusService.FormatDuration(remaining);
        TimerRemainingText.Text = text;

        TimerSubtitleText.Text = _focusTimer.Status == FocusTimerRunState.Paused
            ? "Paused"
            : stopwatch
                ? "Stopwatch"
                : _focusTimer.IsPomodoro
                    ? (_focusTimer.IsBreak
                        ? $"Break · round {_focusTimer.PomodoroRound}"
                        : $"Pomodoro · round {_focusTimer.PomodoroRound}")
                    : "Focus session";
        TimerPauseGlyph.Text = _focusTimer.Status == FocusTimerRunState.Running ? "\uE769" : "\uE768";

        CompactFocusTime.Text = text;
        CompactFocusLabel.Text = _focusTimer.Status == FocusTimerRunState.Paused
            ? "Paused"
            : stopwatch ? "Stopwatch" : "Focus";

        DrawTimerArc(stopwatch ? 0 : _focusTimer.Progress);
        DrawCompactTimerArc(stopwatch ? 0 : _focusTimer.Progress);
    }

    private void RenderFocusComplete()
    {
        FocusCompleteSubtitle.Text = _focusCompletedMinutes > 0
            ? $"{_focusCompletedMinutes} min session"
            : "Session complete";
    }

    /// <summary>Draws the green progress arc of the timer ring as a real geometry.</summary>
    private void DrawTimerArc(double progress)
        => DrawRingArc(TimerProgressArc, progress, 104, 6);

    private void DrawCompactTimerArc(double progress)
        => DrawRingArc(CompactTimerArc, progress, 26, 3);

    private static void DrawRingArc(System.Windows.Shapes.Path target, double progress, double size, double thickness)
    {
        progress = Math.Clamp(progress, 0, 1);

        if (progress <= 0.001)
        {
            target.Data = Geometry.Empty;
            return;
        }

        var radius = (size - thickness) / 2;
        var center = size / 2;
        const double startAngle = -90;
        var sweep = Math.Min(360 * progress, 359.99);

        var start = PointOnCircle(center, center, radius, startAngle);
        var end = PointOnCircle(center, center, radius, startAngle + sweep);

        var figure = new PathFigure { StartPoint = start, IsClosed = false };
        figure.Segments.Add(new ArcSegment(
            end,
            new Size(radius, radius),
            0,
            sweep > 180,
            SweepDirection.Clockwise,
            true));

        target.Data = new PathGeometry(new[] { figure });
    }

    private static Point PointOnCircle(double cx, double cy, double radius, double angleDegrees)
    {
        var radians = angleDegrees * Math.PI / 180;
        return new Point(cx + radius * Math.Cos(radians), cy + radius * Math.Sin(radians));
    }

    // --------------------------------------------------------- notification card

    private void RenderNotificationPanel()
    {
        var info = _pendingNotification;
        if (info is null)
        {
            return;
        }

        var app = info.AppName?.Trim() ?? string.Empty;
        var title = info.Title?.Trim() ?? string.Empty;
        var body = info.Body?.Trim() ?? string.Empty;

        var hasBody = !string.IsNullOrWhiteSpace(body)
            && !string.Equals(body, title, StringComparison.Ordinal);

        if (hasBody)
        {
            NotifHeaderText.Text = string.IsNullOrWhiteSpace(title)
                ? (string.IsNullOrWhiteSpace(app) ? "Notification" : app)
                : (string.IsNullOrWhiteSpace(app) ? title : $"{title} · {app}");
            NotifBodyText.Text = body;
        }
        else
        {
            // Some toasts only have one text line. Show the app as the header
            // and the message once, instead of printing the same text twice.
            NotifHeaderText.Text = string.IsNullOrWhiteSpace(app) ? "Notification" : app;
            NotifBodyText.Text = title;
        }

        var seed = string.IsNullOrWhiteSpace(title) ? app : title;
        AvatarText.Text = seed.Length > 0 ? char.ToUpperInvariant(seed[0]).ToString() : "!";
        AvatarBorder.Background = SystemStatusService.AccentFor(seed);

        // Only message-style toasts get a Reply entry: Windows exposes no reply
        // API to listeners, so the button opens the toast's own reply box.
        NotifReplyButton.Visibility = _settings.NotificationReplyEnabled
            && info.CanReply
            && IsConversationApp(app)
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private static bool IsConversationApp(string app)
    {
        if (string.IsNullOrWhiteSpace(app))
        {
            return false;
        }

        string[] apps =
        {
            "messages", "phone", "whatsapp", "telegram", "signal", "discord", "slack",
            "teams", "messenger", "instagram", "wechat", "viber", "line", "element",
            "skype", "google messages", "imessage", "chat"
        };

        return apps.Any(name => app.Contains(name, StringComparison.OrdinalIgnoreCase));
    }

    private void NotifOpen_Click(object sender, RoutedEventArgs e)
    {
        OpenActionCenter();
        FinishOverlay(IslandState.Notification);
    }

    private void NotifReply_Click(object sender, RoutedEventArgs e)
    {
        var info = _pendingNotification;
        OpenActionCenter();
        FinishOverlay(IslandState.Notification);

        // Be explicit instead of pretending the island sent a message: the
        // inline reply belongs to the toast and only the notification centre
        // can drive it for an unpackaged listener.
        ShowLiveActivity(
            "Reply in the notification centre",
            string.IsNullOrWhiteSpace(info?.AppName) ? "Type in the toast's reply box" : $"Reply to {info!.AppName} there",
            "\uE8BD");
    }

    private static void OpenActionCenter()
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", "ms-actioncenter:")
            {
                UseShellExecute = true
            });
        }
        catch
        {
            // The shell may have no notification centre on stripped-down images.
        }
    }

    private void NotifDismiss_Click(object sender, RoutedEventArgs e)
    {
        var info = _pendingNotification;
        if (info is not null && info.Id != 0)
        {
            // Removes the real toast from the Action Center, not just our card.
            _notifications.Dismiss(info.Id);
        }

        FinishOverlay(IslandState.Notification);
    }

    // ---------------------------------------------------------------- file shelf

    private void OnShelfChanged()
    {
        if (Dispatcher.HasShutdownStarted)
        {
            return;
        }

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_state == IslandState.Shelf)
            {
                BuildShelfItems();
            }
        }));
    }

    private void BuildShelfItems()
    {
        if (ShelfItemsPanel is null)
        {
            return;
        }

        ShelfItemsPanel.Children.Clear();
        var items = _shelf.Items;
        ShelfCountText.Text = items.Count == 0 ? "empty" : $"{items.Count} / 12";
        ShelfEmptyState.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var item in items)
        {
            var row = new Border
            {
                Style = (Style)FindResource("InteractiveListCard"),
                Padding = new Thickness(9, 2, 4, 2),
                Tag = item.Path,
                Cursor = Cursors.Hand,
                ToolTip = item.Path
            };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var glyph = new TextBlock
            {
                Text = "\uE8A5",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 13,
                Foreground = (Brush)FindResource(item.Exists ? "SecondaryBrush" : "TrackBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(3, 0, 9, 0)
            };

            var label = new TextBlock
            {
                Text = item.Exists ? item.Name : $"{item.Name}  (missing)",
                FontFamily = (FontFamily)FindResource("UiText"),
                FontSize = 12,
                Foreground = (Brush)FindResource(item.Exists ? "PrimaryBrush" : "SecondaryBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };

            var remove = new Button
            {
                Content = "\uE711",
                Style = (Style)FindResource("IconButton"),
                Width = 24,
                Height = 24,
                FontSize = 10.5,
                ToolTip = "Remove from shelf"
            };
            remove.Click += (_, _) => _shelf.Remove(item.Path);
            AutomationProperties.SetName(remove, $"Remove {item.Name} from the shelf");

            Grid.SetColumn(label, 1);
            Grid.SetColumn(remove, 2);
            grid.Children.Add(glyph);
            grid.Children.Add(label);
            grid.Children.Add(remove);
            row.Child = grid;

            var path = item.Path;
            row.PreviewMouseLeftButtonDown += (_, e) =>
            {
                if (e.ClickCount == 2)
                {
                    OpenShelfItem(path);
                    e.Handled = true;
                    return;
                }

                _shelfDragStart = e.GetPosition(null);
                _shelfDragPath = path;
            };
            row.GotKeyboardFocus += (_, _) => row.BringIntoView();
            row.KeyDown += (_, e) =>
            {
                if (e.Key is Key.Enter or Key.Space)
                {
                    OpenShelfItem(path);
                    e.Handled = true;
                }
                else if (e.Key == Key.Delete)
                {
                    _shelf.Remove(path);
                    e.Handled = true;
                }
            };
            AutomationProperties.SetName(row, $"{item.Name}, file shelf item");
            AutomationProperties.SetHelpText(row, "Press Enter to open, or Delete to remove from the shelf.");
            row.MouseMove += (_, e) =>
            {
                if (e.LeftButton != MouseButtonState.Pressed || _shelfDragPath != path)
                {
                    return;
                }

                var delta = e.GetPosition(null) - _shelfDragStart;
                if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance
                    && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
                {
                    return;
                }

                _shelfDragPath = null;
                try
                {
                    var data = new DataObject(DataFormats.FileDrop, new[] { path });
                    DragDrop.DoDragDrop(row, data, DragDropEffects.Copy);
                }
                catch
                {
                    // Explorer can refuse a drag mid-operation; the shelf stays intact.
                }
            };
            row.MouseLeftButtonUp += (_, _) => _shelfDragPath = null;

            ShelfItemsPanel.Children.Add(row);
        }
    }

    private static void OpenShelfItem(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch
        {
            // The file may have moved since it was added.
        }
    }

    private void ShelfEntry_Click(object sender, RoutedEventArgs e)
    {
        if (!_settings.ShelfEnabled)
        {
            return;
        }

        SetPinned(true);
        Transition(IslandState.Shelf);
    }

    private static string FormatRate(double bytesPerSecond)
    {
        if (bytesPerSecond >= 1024 * 1024)
        {
            return $"{bytesPerSecond / (1024 * 1024):0.0} MB/s";
        }

        if (bytesPerSecond >= 1024)
        {
            return $"{bytesPerSecond / 1024:0} KB/s";
        }

        return $"{bytesPerSecond:0} B/s";
    }

    private void ShelfClear_Click(object sender, RoutedEventArgs e) => _shelf.Clear();

    private void Shell_DragEnter(object sender, DragEventArgs e)
    {
        e.Effects = _settings.ShelfEnabled && e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void Shell_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = _settings.ShelfEnabled && e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void Shell_Drop(object sender, DragEventArgs e)
    {
        if (!_settings.ShelfEnabled || !e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            return;
        }

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0)
        {
            return;
        }

        if (_shelf.Add(files) > 0)
        {
            SetPinned(true);
            Transition(IslandState.Shelf);
        }

        e.Handled = true;
    }

    // ------------------------------------------------------------- quick notes

    private void OnNotesChanged()
    {
        if (Dispatcher.HasShutdownStarted)
        {
            return;
        }

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_state == IslandState.Notes)
            {
                BuildNotesItems();
            }
        }));
    }

    private void BuildNotesItems()
    {
        if (NotesItemsPanel is null)
        {
            return;
        }

        NotesItemsPanel.Children.Clear();
        var notes = _notes.Items;
        NotesCountText.Text = notes.Count == 0 ? "empty" : $"{notes.Count} / 40";
        ShowNotesError(_notes.LastError);

        if (NotesEmptyState is not null)
        {
            NotesEmptyState.Visibility = notes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        if (notes.Count == 0)
        {
            return;
        }

        foreach (var note in notes)
        {
            var row = new Border
            {
                Style = (Style)FindResource("ListCard"),
                Padding = new Thickness(11, 2, 6, 2)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var check = new System.Windows.Controls.CheckBox
            {
                Style = (Style)FindResource("NoteCheckBox"),
                IsChecked = note.Done,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(1, 0, 9, 0)
            };
            AutomationProperties.SetName(check, $"Mark {note.Text} as done");
            var id = note.Id;
            check.Checked += (_, _) => ShowNotesError(_notes.Toggle(id).Ok ? string.Empty : _notes.LastError);
            check.Unchecked += (_, _) => ShowNotesError(_notes.Toggle(id).Ok ? string.Empty : _notes.LastError);

            var text = new TextBlock
            {
                Text = note.Text,
                FontFamily = (FontFamily)FindResource("UiText"),
                FontSize = 12,
                Foreground = (Brush)FindResource(note.Done ? "SecondaryBrush" : "PrimaryBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                TextDecorations = note.Done ? TextDecorations.Strikethrough : null
            };

            var remove = new Button
            {
                Content = "\uE711",
                Style = (Style)FindResource("IconButton"),
                Width = 24,
                Height = 24,
                FontSize = 10,
                ToolTip = "Delete note"
            };
            remove.Click += (_, _) => ShowNotesError(_notes.Remove(id).Ok ? string.Empty : _notes.LastError);
            AutomationProperties.SetName(remove, "Delete note");

            Grid.SetColumn(text, 1);
            Grid.SetColumn(remove, 2);
            grid.Children.Add(check);
            grid.Children.Add(text);
            grid.Children.Add(remove);
            row.Child = grid;
            NotesItemsPanel.Children.Add(row);
        }
    }

    private void NotesEntry_Click(object sender, RoutedEventArgs e) => ShowNotes(focusKeyboard: true);

    private void NoteAdd_Click(object sender, RoutedEventArgs e) => AddNoteFromInput();

    private void NoteInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            AddNoteFromInput();
            e.Handled = true;
        }
    }

    private void AddNoteFromInput()
    {
        var text = NoteInput.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var result = _notes.Add(text);
        if (!result.Ok)
        {
            // Keep the typed text so a retry costs nothing.
            ShowNotesError(result.Message);
            NoteInput.Focus();
            return;
        }

        ShowNotesError(string.Empty);
        NoteInput.Clear();
        NoteInput.Focus();
    }

    private void ShowNotesError(string message)
    {
        if (NotesErrorText is null)
        {
            return;
        }

        NotesErrorText.Text = message ?? string.Empty;
        NotesErrorText.Visibility = string.IsNullOrWhiteSpace(message)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    // --------------------------------------------------------------- charging HUD

    private void RenderChargingPanel()
    {
        ChargingPercentText.Text = $"{(int)Math.Round(_chargingPercent)}%";
        ChargingBatteryFill.Width = Math.Max(2, ChargingBatteryMaxWidth * Math.Clamp(_chargingPercent / 100, 0, 1));
    }

    // ----------------------------------------------------------------- volume HUD

    private void RenderVolumePanel()
    {
        VolumeGlyph.Text = _muted || _volumeLevel <= 0.001 ? "\uE74F" : "\uE767";
        VolumeFill.Width = VolumeTrackWidth * Math.Clamp(_volumeLevel, 0, 1);
        VolumeText.Text = $"{(int)Math.Round(_volumeLevel * 100)}";
    }

    private void RenderBrightnessPanel()
    {
        BrightnessGlyph.Text = _brightnessLevel <= 0.01 ? "\uE708" : "\uE706";
        BrightnessFill.Width = 250 * Math.Clamp(_brightnessLevel, 0, 1);
        BrightnessText.Text = $"{(int)Math.Round(_brightnessLevel * 100)}";
    }

    private void OnBrightnessChanged(int percent)
    {
        if (Dispatcher.HasShutdownStarted)
        {
            return;
        }

        Dispatcher.BeginInvoke(new Action(() =>
        {
            _brightnessLevel = Math.Clamp(percent / 100.0, 0, 1);

            if (_paused || !_settings.BrightnessEnabled)
            {
                return;
            }

            if (_state is IslandState.Notification or IslandState.FocusComplete)
            {
                return;
            }

            PushOverlay(IslandState.Brightness);
            _brightnessCountdown.Stop();
            _brightnessCountdown.Start();
        }));
    }

    private void OnVolumeChanged(double level, bool muted)
    {
        if (Dispatcher.HasShutdownStarted)
        {
            return;
        }

        Dispatcher.BeginInvoke(new Action(() =>
        {
            _volumeLevel = Math.Clamp(level, 0, 1);
            _muted = muted;

            if (_paused || !_settings.VolumeEnabled)
            {
                return;
            }

            // Never cut off an attention card with the volume HUD.
            if (_state is IslandState.Notification or IslandState.FocusComplete or IslandState.Brightness)
            {
                return;
            }

            PushOverlay(IslandState.Volume);
            _volumeCountdown.Stop();
            _volumeCountdown.Start();
        }));
    }

    // ---------------------------------------------------------------- overlays

    private static bool IsOverlay(IslandState state)
        => state is IslandState.Notification or IslandState.Charging or IslandState.Volume
            or IslandState.Brightness or IslandState.FocusComplete or IslandState.LiveActivity;

    private void PushOverlay(IslandState overlay)
    {
        if (_paused)
        {
            return;
        }

        // A peek must not cost the user the panel they were working in.
        if (!IsOverlay(_state) && _pinned)
        {
            _overlayReturnState = _state;
        }

        _splashActive = false;
        _splash.Stop();
        Transition(overlay);
    }

    /// <summary>
    /// Drops whichever transient peek is on screen and lands on the activity
    /// underneath. Callers decide whether the pin survives the dismissal.
    /// </summary>
    private void DismissActiveOverlay()
    {
        _chargingCountdown.Stop();
        _volumeCountdown.Stop();
        _brightnessCountdown.Stop();
        _focusCompleteCountdown.Stop();
        _liveActivityCountdown.Stop();
        _notificationCountdown.Stop();
        _notificationQueue.Clear();
        _pendingNotification = null;
        _overlayReturnState = null;
        Transition(ResolveResting());
    }

    private void FinishOverlay(IslandState overlay)
    {
        if (overlay == IslandState.Notification)
        {
            _notificationCountdown.Stop();

            // Another state owns the surface: keep the queue untouched for the
            // next real notification instead of advancing it invisibly.
            if (_state != IslandState.Notification)
            {
                ClearNotificationSession();
                return;
            }

            _pendingNotification = null;

            if (_notificationQueue.TryDequeue(out var next))
            {
                _pendingNotification = next.Info;
                _notificationCountdown.Interval = next.Duration;
                _notificationCountdown.Start();
                RenderState();
                return;
            }
        }
        else if (overlay == IslandState.Charging)
        {
            _chargingCountdown.Stop();
        }
        else if (overlay == IslandState.Volume)
        {
            _volumeCountdown.Stop();
        }
        else if (overlay == IslandState.Brightness)
        {
            _brightnessCountdown.Stop();
        }
        else if (overlay == IslandState.FocusComplete)
        {
            _focusCompleteCountdown.Stop();
        }
        else if (overlay == IslandState.LiveActivity)
        {
            _liveActivityCountdown.Stop();
        }

        if (_state == overlay)
        {
            if (_overlayReturnState is { } previous && _pinned && !IsOverlay(previous))
            {
                _overlayReturnState = null;
                Transition(previous);
                return;
            }

            _overlayReturnState = null;
            Transition(ResolveResting());
        }
    }

    private IslandState ResolveResting()
    {
        var hasFocus = _settings.FocusEnabled && _focusTimer.Status != FocusTimerRunState.Stopped;
        var hasMedia = _settings.MediaEnabled && _currentMedia?.HasMedia == true;

        if (hasFocus && hasMedia)
        {
            return _settings.MediaFirst ? IslandState.CompactMedia : IslandState.CompactFocus;
        }

        if (hasFocus)
        {
            return IslandState.CompactFocus;
        }

        if (hasMedia)
        {
            return IslandState.CompactMedia;
        }

        return IslandState.Idle;
    }

    // ---------------------------------------------------------------- services

    private void OnMediaChanged(MediaInfo? info)
    {
        if (Dispatcher.HasShutdownStarted)
        {
            return;
        }

        Dispatcher.BeginInvoke(() => ApplyMedia(info));
    }

    private void ApplyMedia(MediaInfo? info)
    {
        if (!_settings.MediaEnabled)
        {
            _currentMedia = info;
            if (_state is IslandState.CompactMedia or IslandState.ExpandedMedia)
            {
                Transition(ResolveResting());
            }

            return;
        }

        var previous = _currentMedia;

        var isNewTrack = previous is null
            || info is null
            || !info.HasMedia
            || previous.Title != info.Title
            || previous.SourceApp != info.SourceApp;

        _currentMedia = info;

        if (isNewTrack)
        {
            _position = TimeSpan.Zero;
            _duration = TimeSpan.Zero;
        }

        if (info is null || !info.HasMedia)
        {
            _splash.Stop();
            _splashActive = false;

            if (!_paused && _state is IslandState.CompactMedia or IslandState.ExpandedMedia)
            {
                Transition(ResolveResting());
            }

            return;
        }

        if (_paused)
        {
            return;
        }

        if (_state is IslandState.CompactMedia or IslandState.ExpandedMedia)
        {
            // Refresh in place. A new track must never pop the full player open:
            // media stays compact until the user hovers it or picks it from the tray.
            RenderState();
            return;
        }

        // The clock panel is open because the pointer is on it; let it close
        // naturally. HoverLeave resolves to ResolveResting(), which will reveal
        // the compact player instead of dropping back to Idle.
        if (_state == IslandState.IdleExpanded)
        {
            return;
        }

        if (_state is IslandState.Idle or IslandState.IdleHidden)
        {
            // MediaFirst decides between the focus bar and the player bar when
            // both activities are live.
            Transition(ResolveResting());
        }
    }

    private void Splash_Tick(object? sender, EventArgs e)
    {
        _splash.Stop();
        _splashActive = false;

        // A pin outranks the splash; collapsing under it would undo the pin.
        if (_pinned || Shell.IsMouseOver || _state != IslandState.ExpandedMedia)
        {
            return;
        }

        Transition(_focusTimer.Status != FocusTimerRunState.Stopped
            ? IslandState.FocusTimer
            : IslandState.CompactMedia);
    }

    private void OnTimelineChanged(MediaTimeline timeline)
    {
        if (Dispatcher.HasShutdownStarted)
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            _position = timeline.Position;
            _duration = timeline.Duration;
            UpdateProgressVisual();
        });
    }

    private void OnNotificationArrived(NotificationInfo info)
    {
        if (Dispatcher.HasShutdownStarted)
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            if (_quietMode || _paused || !_settings.NotificationsEnabled)
            {
                return;
            }

            if (MatchesAppList(info.AppName, _settings.BlockedApps))
            {
                return;
            }

            ShowNotificationCard(
                info,
                MatchesAppList(info.AppName, _settings.PriorityApps)
                    ? TimeSpan.FromSeconds(_settings.NotificationVisibleSeconds * 2.0)
                    : TimeSpan.FromSeconds(_settings.NotificationVisibleSeconds));
        });
    }

    private void ClearNotificationSession()
    {
        _notificationCountdown.Stop();
        _notificationQueue.Clear();
        _pendingNotification = null;
    }

    private void ShowNotificationCard(NotificationInfo info, TimeSpan duration)
    {
        if (_state == IslandState.Notification)
        {
            // Three peeks waiting is plenty; the oldest is dropped beyond that.
            _notificationQueue.TryEnqueue((info, duration));
            return;
        }

        _pendingNotification = info;
        _notificationCountdown.Interval = duration;
        PushOverlay(IslandState.Notification);
        _notificationCountdown.Stop();
        _notificationCountdown.Start();
        FeedbackService.Notify();
    }

    private void OnApiActivity(IslandActivity activity)
    {
        if (Dispatcher.HasShutdownStarted)
        {
            return;
        }

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_paused || !_settings.NotificationsEnabled)
            {
                return;
            }

            ShowNotificationCard(
                new NotificationInfo("Tolk Island API", activity.Title, activity.Body),
                TimeSpan.FromSeconds(Math.Clamp(activity.DurationSeconds, 1, 60)));
        }));
    }

    private void OnApiPanel(string panel)
    {
        if (Dispatcher.HasShutdownStarted)
        {
            return;
        }

        Dispatcher.BeginInvoke(new Action(() =>
        {
            switch (panel)
            {
                case "shelf":
                    ShowShelf();
                    break;
                case "notes":
                    ShowNotes();
                    break;
                case "media":
                    ExpandNowPlaying();
                    break;
                case "idle":
                    SetPinned(false);
                    Transition(ResolveResting());
                    break;
                case "focus":
                    SetPinned(true);
                    Transition(IslandState.FocusPicker);
                    break;
            }
        }));
    }

    private void OnApiFocus(int seconds)
    {
        if (Dispatcher.HasShutdownStarted)
        {
            return;
        }

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (seconds <= 0)
            {
                _focusTimer.StartStopwatch();
            }
            else
            {
                _focusTimer.Start(TimeSpan.FromSeconds(seconds));
            }
        }));
    }

    private void OnApiMedia(string action, int positionSeconds)
    {
        if (Dispatcher.HasShutdownStarted)
        {
            return;
        }

        Dispatcher.BeginInvoke(new Action(() =>
        {
            switch (action)
            {
                case "playpause":
                    _ = _media.TogglePlayPauseAsync();
                    break;
                case "next":
                    _ = _media.SkipNextAsync();
                    break;
                case "previous":
                    _ = _media.SkipPreviousAsync();
                    break;
                case "seek":
                    _position = TimeSpan.FromSeconds(Math.Max(0, positionSeconds));
                    UpdateProgressVisual();
                    _ = _media.SeekAsync(_position);
                    break;
            }
        }));
    }

    private void OnApiShelf(string path)
    {
        if (Dispatcher.HasShutdownStarted)
        {
            return;
        }

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_settings.ShelfEnabled || _shelf.Add(new[] { path }) == 0)
            {
                return;
            }

            SetPinned(true);
            Transition(IslandState.Shelf);
        }));
    }

    private static bool MatchesAppList(string appName, IReadOnlyList<string> rules)
    {
        if (string.IsNullOrWhiteSpace(appName) || rules.Count == 0)
        {
            return false;
        }

        return rules.Any(rule => !string.IsNullOrWhiteSpace(rule)
            && (appName.Contains(rule.Trim(), StringComparison.OrdinalIgnoreCase)
                || rule.Trim().Contains(appName, StringComparison.OrdinalIgnoreCase)));
    }

    private void OnFocusTimerStateChanged()
    {
        if (Dispatcher.HasShutdownStarted)
        {
            return;
        }

        // Snapshot the state at event time; the dispatcher callback may run after
        // another start/pause and must not mistake that for a completion.
        var status = _focusTimer.Status;
        var target = _focusTimer.Target;
        var elapsed = _focusTimer.Elapsed;

        Dispatcher.BeginInvoke(() => ApplyFocusTimerState(status, target, elapsed));
    }

    private void ApplyFocusTimerState(FocusTimerRunState status, TimeSpan target, TimeSpan elapsed)
    {
        var previous = _lastFocusStatus;
        _lastFocusStatus = status;

        if (_paused)
        {
            return;
        }

        if (status == FocusTimerRunState.Stopped)
        {
            var completed = previous != FocusTimerRunState.Stopped
                && target > TimeSpan.Zero
                && elapsed >= target;

            if (completed)
            {
                _focusCompletedMinutes = target >= TimeSpan.FromMinutes(1)
                    ? (int)Math.Round(target.TotalMinutes)
                    : 0;
                ShowFocusComplete();
            }
            else if (_state is IslandState.FocusTimer or IslandState.CompactFocus)
            {
                Transition(ResolveResting());
            }

            return;
        }

        if (IsOverlay(_state))
        {
            return;
        }

        if (_paused)
        {
            return;
        }

        if (_state is IslandState.FocusTimer or IslandState.CompactFocus)
        {
            UpdateTimerVisual();
            return;
        }

        _splash.Stop();
        _splashActive = false;
        Transition(IslandState.CompactFocus);
    }

    private void ShowFocusComplete()
    {
        if (_paused)
        {
            return;
        }

        _focusCompleteCountdown.Stop();

        // Route through the overlay path so a pinned panel is restored after the
        // card rather than being replaced by the resting state.
        PushOverlay(IslandState.FocusComplete);
        _focusCompleteCountdown.Stop();
        _focusCompleteCountdown.Start();
    }

    private void OnFocusTimerElapsed(TimeSpan elapsed)
    {
        if (Dispatcher.HasShutdownStarted)
        {
            return;
        }

        Dispatcher.BeginInvoke(new Action(UpdateTimerVisual));
    }

    private void OnHeartbeat()
    {
        // The clock text only exists in the resting pill and the home panel.
        if (_state is IslandState.Idle or IslandState.IdleExpanded)
        {
            UpdateClock();
        }

        // Charging must be noticed even while the island is hidden or on a panel.
        if (++_heartbeatTicks % 3 == 0)
        {
            UpdateBattery();
        }

        if (_state == IslandState.Idle)
        {
            CheckIdleHide();
        }

        CheckFullscreenApp();
    }

    private void UpdateClock()
    {
        var now = DateTime.Now;
        var time = SystemStatusService.FormatClock(now);
        if (ClockText.Text != time)
        {
            ClockText.Text = time;
            BigClockText.Text = time;
        }

        var date = now.ToString("dddd, d MMMM");
        if (DateText.Text != date)
        {
            DateText.Text = date;
        }
    }

    private void UpdateBattery()
    {
        var (hasBattery, onAc, percent, isCharging) = SystemStatusService.GetPowerStatus();
        var rounded = (int)Math.Round(percent);

        var text = hasBattery ? $"{rounded}%" : onAc ? "AC" : "\u2014";
        if (BatteryText.Text != text)
        {
            BatteryText.Text = text;
            HomeBatteryText.Text = text;
        }

        var fillWidth = hasBattery ? Math.Round(12 * Math.Clamp(percent / 100.0, 0, 1)) : 0;
        if (Math.Abs(BatteryFill.Width - fillWidth) > 0.1)
        {
            BatteryFill.Width = fillWidth;
        }

        if (Math.Abs(HomeBatteryFill.Width - fillWidth) > 0.1)
        {
            HomeBatteryFill.Width = fillWidth;
        }

        var low = hasBattery && !isCharging && rounded <= 15;
        var fillBrush = (Brush)FindResource(
            isCharging ? "GreenBrush" : low ? "DangerBrush" : "PrimaryBrush");
        if (!ReferenceEquals(BatteryFill.Background, fillBrush))
        {
            BatteryFill.Background = fillBrush;
            HomeBatteryFill.Background = fillBrush;
            BatteryText.Foreground = (Brush)FindResource(
                isCharging ? "GreenBrush" : low ? "DangerBrush" : "SecondaryBrush");
            HomeBatteryText.Foreground = BatteryText.Foreground;
        }

        var tip = hasBattery
            ? $"Battery {rounded}%{(isCharging ? " · charging" : onAc ? " · plugged in" : string.Empty)}"
            : onAc ? "No battery · AC power" : "Battery status unavailable";
        if (!Equals(BatteryGraphic.ToolTip, tip))
        {
            BatteryGraphic.ToolTip = tip;
            HomeBatteryGraphic.ToolTip = tip;
            HomeBatteryText.ToolTip = tip;
        }

        if (_lastOnAc == false && onAc && hasBattery && isCharging && _settings.ChargingEnabled)
        {
            _chargingPercent = percent;
            if (_state is not (IslandState.Notification or IslandState.FocusComplete))
            {
                PushOverlay(IslandState.Charging);
                _chargingCountdown.Stop();
                _chargingCountdown.Start();
            }
        }

        _lastOnAc = onAc;
    }

    // ---------------------------------------------------- fullscreen auto-hide

    private void CheckFullscreenApp()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(CheckFullscreenApp);
            return;
        }

        if (_paused)
        {
            return;
        }

        var hwnd = NativeMethods.GetForegroundWindow();
        var self = _windowHandle;

        if (hwnd == IntPtr.Zero || hwnd == self)
        {
            _fullscreenTicks = 0;
            ShowIsland();
            return;
        }

        if (_settings.MonitorMode == MonitorMode.ActiveWindow)
        {
            PositionWindow();
        }

        // The desktop, taskbar and our own window are never "fullscreen apps".
        _classNameBuffer.Clear();
        if (NativeMethods.GetClassName(hwnd, _classNameBuffer, _classNameBuffer.Capacity) > 0)
        {
            var name = _classNameBuffer.ToString();
            if (name is "Progman" or "WorkerW" or "Shell_TrayWnd")
            {
                _fullscreenTicks = 0;
                ShowIsland();
                return;
            }
        }

        var foregroundScreen = System.Windows.Forms.Screen.FromHandle(hwnd);
        var ownScreen = ResolveTargetScreen();
        if (ownScreen is not null
            && foregroundScreen is not null
            && !string.Equals(ownScreen.DeviceName, foregroundScreen.DeviceName, StringComparison.OrdinalIgnoreCase))
        {
            _fullscreenTicks = 0;
            ShowIsland();
            return;
        }

        var bounds = foregroundScreen?.Bounds ?? ownScreen?.Bounds ?? System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
        var isFullscreen = false;

        if (NativeMethods.GetWindowRect(hwnd, out var rect))
        {
            var coversScreen =
                rect.Left <= bounds.Left &&
                rect.Top <= bounds.Top &&
                rect.Right >= bounds.Right &&
                rect.Bottom >= bounds.Bottom;

            // Maximized apps keep their title bar style; true borderless
            // fullscreen (games, F11 video) drops it. Borderless *maximized*
            // apps (Chrome, Electron) also drop the caption, so a maximize
            // style must not be mistaken for fullscreen. The shell's own
            // fullscreen state catches true fullscreen/presentation windows
            // that happen to be maximized.
            var style = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_STYLE);
            var hasCaption = (style & NativeMethods.WS_CAPTION) != 0;
            var isMaximized = (style & NativeMethods.WS_MAXIMIZE) != 0;
            var shellFullscreen = NativeMethods.SHQueryUserNotificationState(out var notificationState) == 0
                && notificationState is 2 or 3 or 4;

            isFullscreen = coversScreen && (shellFullscreen || (!hasCaption && !isMaximized));
        }

        if (isFullscreen)
        {
            // Hysteresis: only hide once the condition held for two polls,
            // so alt-tab flashes never make the island flicker away.
            if (++_fullscreenTicks >= 2)
            {
                HideIsland();
            }
        }
        else
        {
            _fullscreenTicks = 0;
            ShowIsland();
        }
    }

    private void HideIsland()
    {
        if (Visibility != Visibility.Visible)
        {
            return;
        }

        // A hidden camera panel must not leave a stale frame for the next show.
        if (_state == IslandState.Mirror)
        {
            Transition(ResolveResting());
        }

        SetPinned(false);
        _hoverLeave.Stop();
        Visibility = Visibility.Hidden;
        SyncCapture();
        SyncMirror();
    }

    private void ShowIsland()
    {
        if (_paused || Visibility == Visibility.Visible)
        {
            return;
        }

        PositionWindow(force: true);
        _lastPresence = DateTime.UtcNow;
        Visibility = Visibility.Visible;
        PinTopMost(new WindowInteropHelper(this).Handle);
        SyncCapture();
        SyncMirror();

        // A fullscreen app can hide the island while it is expanded (no MouseLeave
        // reaches us). Re-arm the collapse timer so it does not stay expanded
        // forever once the island comes back.
        if (!_pinned && !_splashActive && (_state is IslandState.IdleExpanded or IslandState.ExpandedMedia
            or IslandState.FocusTimer or IslandState.FocusPicker))
        {
            _hoverLeave.Stop();
            _hoverLeave.Start();
        }
    }

    // ---------------------------------------------------------------- waveform

    private void BuildWaveform()
    {
        var brush = (Brush)FindResource("WaveBrush");

        for (var i = 0; i < 5; i++)
        {
            var bar = new Border
            {
                Width = 3.5,
                Height = 24,
                CornerRadius = new CornerRadius(1.75),
                Margin = new Thickness(2.5, 0, 2.5, 0),
                Background = brush,
                RenderTransform = new ScaleTransform(1, WaveformFloor),
                RenderTransformOrigin = new System.Windows.Point(0.5, 0.5)
            };

            _waveformBars.Add(bar);
            WaveformHost.Children.Add(bar);
        }
    }

    private void PulseWaveform()
    {
        if (!_visualizer.TryGetLevels(_waveformLevels))
        {
            return;
        }

        for (var i = 0; i < _waveformBars.Count && i < _waveformLevels.Length; i++)
        {
            if (_waveformBars[i].RenderTransform is not ScaleTransform scale)
            {
                continue;
            }

            scale.ScaleY = WaveformFloor + (1 - WaveformFloor) * _waveformLevels[i];
        }
    }

    // --------------------------------------------------------------- interaction

    private void Shell_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            CycleActivity(e.Delta > 0 ? -1 : 1);
            e.Handled = true;
            return;
        }

        if (!_settings.VolumeEnabled)
        {
            return;
        }

        if (_audio.AdjustVolume(e.Delta > 0 ? 0.02 : -0.02))
        {
            e.Handled = true;
        }
    }

    private void Shell_MouseEnter(object sender, RoutedEventArgs e)
    {
        _hoverLeave.Stop();
        _lastPresence = DateTime.UtcNow;

        if (_pinned)
        {
            return;
        }

        if (_settings.ExpandMode == ExpandMode.Click)
        {
            return;
        }

        if (_state is IslandState.Idle or IslandState.IdleHidden)
        {
            Transition(IslandState.IdleExpanded);
        }
        else if (_state == IslandState.CompactMedia)
        {
            Transition(IslandState.ExpandedMedia);
        }
        else if (_state == IslandState.CompactFocus)
        {
            Transition(IslandState.FocusTimer);
        }
    }

    private void Shell_MouseLeave(object sender, RoutedEventArgs e)
    {
        _lastPresence = DateTime.UtcNow;

        if (_pinned)
        {
            return;
        }

        if (_state is IslandState.ExpandedMedia or IslandState.IdleExpanded
            or IslandState.FocusTimer or IslandState.FocusPicker or IslandState.Shelf
            or IslandState.Notes or IslandState.Calendar or IslandState.Prompt or IslandState.Mirror)
        {
            _hoverLeave.Stop();
            _hoverLeave.Start();
        }
    }

    private void Shell_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (IsInteractiveSource(e.OriginalSource) || IsOverlay(_state))
        {
            return;
        }

        PinCurrent(focusKeyboard: true);
    }

    private static bool IsInteractiveSource(object? source)
    {
        var current = source as DependencyObject;
        while (current is not null && current is not Window)
        {
            if (current is System.Windows.Controls.Primitives.ButtonBase)
            {
                return true;
            }

            try
            {
                current = VisualTreeHelper.GetParent(current);
            }
            catch
            {
                return false;
            }
        }

        return false;
    }

    private void PinCurrent(bool focusKeyboard = false)
    {
        if (IsOverlay(_state))
        {
            return;
        }

        _lastPresence = DateTime.UtcNow;
        SetPinned(true, focusKeyboard);

        switch (_state)
        {
            case IslandState.Idle:
            case IslandState.IdleHidden:
                Transition(IslandState.IdleExpanded);
                break;

            case IslandState.CompactMedia:
                Transition(IslandState.ExpandedMedia);
                break;

            case IslandState.CompactFocus:
                Transition(IslandState.FocusTimer);
                break;

            case IslandState.Shelf:
            case IslandState.Notes:
            case IslandState.Calendar:
            case IslandState.Prompt:
            case IslandState.Mirror:
                break;
        }
    }

    private void SetPinned(bool pinned, bool focusKeyboard = false)
    {
        if (_pinned == pinned)
        {
            if (pinned && focusKeyboard)
            {
                SetKeyboardFocusMode(true);
            }

            return;
        }

        _pinned = pinned;
        if (pinned)
        {
            _hoverLeave.Stop();
            InstallMouseHook();
            if (focusKeyboard)
            {
                SetKeyboardFocusMode(true);
            }
        }
        else
        {
            UninstallMouseHook();
            SetKeyboardFocusMode(false);
        }
    }

    private void SetKeyboardFocusMode(bool enabled)
    {
        if (_windowHandle == IntPtr.Zero)
        {
            return;
        }

        var ex = NativeMethods.GetWindowLong(_windowHandle, NativeMethods.GWL_EXSTYLE);
        ex = enabled ? ex & ~NativeMethods.WS_EX_NOACTIVATE : ex | NativeMethods.WS_EX_NOACTIVATE;
        NativeMethods.SetWindowLong(_windowHandle, NativeMethods.GWL_EXSTYLE, ex);

        if (enabled)
        {
            // Taking the foreground for keyboard control also takes it from
            // whatever the user was typing in; remember it so Escape can hand
            // focus back instead of leaving them on a desktop with no caret.
            if (_previousForeground == IntPtr.Zero)
            {
                _previousForeground = NativeMethods.GetForegroundWindow();
            }

            Activate();
            NativeMethods.SetForegroundWindow(_windowHandle);
            Dispatcher.BeginInvoke(new Action(() =>
            {
                Focus();
                MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
            }));
        }
        else
        {
            var previous = _previousForeground;
            _previousForeground = IntPtr.Zero;
            if (previous != IntPtr.Zero
                && previous != _windowHandle
                && NativeMethods.GetForegroundWindow() == _windowHandle
                && NativeMethods.IsWindow(previous))
            {
                NativeMethods.SetForegroundWindow(previous);
            }
        }
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _promptRequest.State == PromptState.Working)
        {
            CancelPrompt();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && _pinned)
        {
            ClosePinned();
            e.Handled = true;
        }
    }

    private void ClosePinned()
    {
        if (!_pinned)
        {
            return;
        }

        SetPinned(false);
        if (IsOverlay(_state))
        {
            return;
        }

        var next = ResolveResting();
        if (next == IslandState.Idle && _idleHideEnabled)
        {
            next = IslandState.IdleHidden;
        }

        Transition(next);
    }

    private void InstallMouseHook()
    {
        if (_mouseHook != IntPtr.Zero)
        {
            return;
        }

        _mouseHookProc ??= MouseHookProc;
        _mouseHook = NativeMethods.SetWindowsHookEx(
            NativeMethods.WH_MOUSE_LL,
            _mouseHookProc,
            NativeMethods.GetModuleHandle(null),
            0);
    }

    private void UninstallMouseHook()
    {
        if (_mouseHook == IntPtr.Zero)
        {
            return;
        }

        // If unhook fails the hook is still live; keep its handle (and the
        // rooted delegate) so a later pin or shutdown can retry instead of
        // orphaning a global hook. The callback does nothing while unpinned.
        if (!NativeMethods.UnhookWindowsHookEx(_mouseHook))
        {
            return;
        }

        _mouseHook = IntPtr.Zero;

        // Deliberately keep _mouseHookProc rooted: Windows may still be invoking
        // an in-flight callback for a moment after UnhookWindowsHookEx returns.
    }

    private IntPtr MouseHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0 && _pinned)
            {
                var message = wParam.ToInt32();
                if (message is NativeMethods.WM_LBUTTONDOWN
                    or NativeMethods.WM_RBUTTONDOWN
                    or NativeMethods.WM_MBUTTONDOWN)
                {
                    var info = System.Runtime.InteropServices.Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
                    var point = info.Point;
                    _ = Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (_pinned && !IsPointInsideShell(point))
                        {
                            ClosePinned();
                        }
                    }));
                }
            }
        }
        catch
        {
            // A malformed hook callback must never break the system hook chain.
        }

        return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private bool IsPointInsideShell(NativeMethods.POINT point)
    {
        try
        {
            if (!IsVisible || Shell.ActualWidth <= 0 || Shell.ActualHeight <= 0)
            {
                return false;
            }

            var local = Shell.PointFromScreen(new Point(point.X, point.Y));
            if (local.X < 0 || local.Y < 0
                || local.X > Shell.ActualWidth || local.Y > Shell.ActualHeight)
            {
                return false;
            }

            return ShellShape.Data?.FillContains(local) == true;
        }
        catch
        {
            return false;
        }
    }

    private void CheckIdleHide()
    {
        if (_paused || !_idleHideEnabled || _pinned || _previewActive || !IsLoaded
            || Visibility != Visibility.Visible
            || Shell.IsMouseOver)
        {
            return;
        }

        if (DateTime.UtcNow - _lastPresence >= TimeSpan.FromSeconds(_settings.IdleHideSeconds))
        {
            Transition(IslandState.IdleHidden);
        }
    }

    private void HoverLeave_Tick(object? sender, EventArgs e)
    {
        _hoverLeave.Stop();

        if (_pinned || _previewActive || Shell.IsMouseOver)
        {
            return;
        }

        if (_state == IslandState.ExpandedMedia)
        {
            // If a focus session is still running, closing the player must reveal
            // the compact timer again instead of leaving it invisible behind media.
            Transition(_focusTimer.Status != FocusTimerRunState.Stopped
                ? IslandState.CompactFocus
                : IslandState.CompactMedia);
        }
        else if (_state == IslandState.FocusTimer)
        {
            Transition(_focusTimer.Status != FocusTimerRunState.Stopped
                ? IslandState.CompactFocus
                : ResolveResting());
        }
        else if (_state == IslandState.FocusPicker)
        {
            Transition(ResolveResting());
        }
        else if (_state == IslandState.IdleExpanded)
        {
            // Media may have started while the clock panel was open; reveal the
            // compact player now instead of dropping straight back to Idle.
            Transition(ResolveResting());
        }
        else if (_state is IslandState.Shelf or IslandState.Notes
            or IslandState.Calendar or IslandState.Prompt or IslandState.Mirror)
        {
            Transition(ResolveResting());
        }
    }

    private async void PlayPauseButton_Click(object sender, RoutedEventArgs e)
    {
        // Flip immediately; the session event corrects it if the player refuses.
        // Waiting for the SMTC round trip made the island feel like it missed taps.
        PlayPauseGlyph.Text = _currentMedia?.IsPlaying == true ? "\uE768" : "\uE769";
        await _media.TogglePlayPauseAsync();
        await _media.RefreshAsync();
    }

    private void NextButton_Click(object sender, RoutedEventArgs e)
        => _ = _media.SkipNextAsync();

    private void PrevButton_Click(object sender, RoutedEventArgs e)
        => _ = _media.SkipPreviousAsync();

    private async void ShuffleButton_Click(object sender, RoutedEventArgs e)
    {
        // Paint the new mode before the SMTC round trip; the refresh after the
        // call restores the truth if the source app rejects the change.
        var shuffle = !_media.IsShuffleEnabled;
        ShuffleButton.Opacity = shuffle ? 1 : 0.55;
        ShuffleButton.Foreground = (Brush)FindResource(shuffle ? "BlueBrush" : "PrimaryBrush");
        await _media.ToggleShuffleAsync();

        // ToggleShuffleAsync swallows a rejecting player without refreshing.
        if (_state == IslandState.ExpandedMedia)
        {
            UpdateMediaModeButtons();
        }
    }

    private async void RepeatButton_Click(object sender, RoutedEventArgs e)
    {
        var next = MediaSessionService.NextRepeatMode(_media.RepeatMode);
        RepeatButton.Content = next == MediaPlaybackAutoRepeatMode.Track ? "\uE8ED" : "\uE8EE";
        RepeatButton.Opacity = next == MediaPlaybackAutoRepeatMode.None ? 0.55 : 1;
        RepeatButton.Foreground = (Brush)FindResource(
            next == MediaPlaybackAutoRepeatMode.None ? "PrimaryBrush" : "BlueBrush");
        await _media.CycleRepeatAsync();

        if (_state == IslandState.ExpandedMedia)
        {
            UpdateMediaModeButtons();
        }
    }

    private void ProgressHit_SeekRequested(object? sender, double seconds)
    {
        if (_duration <= TimeSpan.Zero)
        {
            return;
        }

        _position = TimeSpan.FromSeconds(Math.Clamp(seconds, 0, _duration.TotalSeconds));
        UpdateProgressVisual();
        _ = _media.SeekAsync(_position);
    }

    private void ProgressHit_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_duration <= TimeSpan.Zero)
        {
            return;
        }

        _seeking = true;
        ProgressHit.CaptureMouse();
        SeekFromPointer(e.GetPosition(ProgressHit).X);
        e.Handled = true;
    }

    private void ProgressHit_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_seeking || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        SeekFromPointer(e.GetPosition(ProgressHit).X);
    }

    private void ProgressHit_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_seeking)
        {
            return;
        }

        SeekFromPointer(e.GetPosition(ProgressHit).X);
        _seeking = false;
        ProgressHit.ReleaseMouseCapture();
        _ = _media.SeekAsync(_position);
        e.Handled = true;
    }

    private void ProgressHit_LostMouseCapture(object sender, MouseEventArgs e)
    {
        // A drag interrupted by alt-tab or another capture must end the scrub;
        // otherwise the timeline keeps following the pointer with no button held.
        if (!_seeking)
        {
            return;
        }

        _seeking = false;
        _ = _media.SeekAsync(_position);
    }

    private void SeekFromPointer(double x)
    {
        if (_duration <= TimeSpan.Zero || ProgressHit.ActualWidth <= 0)
        {
            return;
        }

        var ratio = Math.Clamp(x / ProgressHit.ActualWidth, 0, 1);
        _position = TimeSpan.FromMilliseconds(_duration.TotalMilliseconds * ratio);
        ProgressHit.Value = _position.TotalSeconds;
        PositionText.Text = SystemStatusService.FormatDuration(_position);
        var track = ProgressTrack.ActualWidth;
        ProgressFill.Width = track * ratio;
        PositionKnob(track, ratio);
    }

    private void FocusEntry_Click(object sender, RoutedEventArgs e)
    {
        if (_state == IslandState.IdleExpanded)
        {
            Transition(IslandState.FocusPicker);
        }
    }

    private void FocusCancel_Click(object sender, RoutedEventArgs e)
    {
        if (_state == IslandState.FocusPicker)
        {
            Transition(ResolveResting());
        }
    }

    private void Focus15_Click(object sender, RoutedEventArgs e) => StartFocusFromPicker(TimeSpan.FromMinutes(15));

    private void Focus25_Click(object sender, RoutedEventArgs e) => StartFocusFromPicker(TimeSpan.FromMinutes(25));

    private void Focus50_Click(object sender, RoutedEventArgs e) => StartFocusFromPicker(TimeSpan.FromMinutes(50));

    private void FocusStopwatch_Click(object sender, RoutedEventArgs e)
    {
        _focusTimer.StartStopwatch();

        if (_state == IslandState.FocusPicker)
        {
            Transition(IslandState.CompactFocus);
        }
    }

    private void StartFocusFromPicker(TimeSpan duration)
    {
        _focusTimer.Start(duration);

        // StateChanged is dispatched asynchronously; collapse immediately so the
        // picker does not linger while the first callback is queued.
        if (_state == IslandState.FocusPicker)
        {
            Transition(IslandState.CompactFocus);
        }
    }

    private void TimerPause_Click(object sender, RoutedEventArgs e)
    {
        // The service dispatches its state change asynchronously; flip the glyph
        // now so the button answers the click on the same frame.
        TimerPauseGlyph.Text =
            _focusTimer.Status == FocusTimerRunState.Running ? "\uE768" : "\uE769";
        _focusTimer.Toggle();
    }

    private void TimerReset_Click(object sender, RoutedEventArgs e)
        => _focusTimer.Reset();

    // --------------------------------------------------------------- calendar

    private void OnCalendarChanged()
    {
        if (Dispatcher.HasShutdownStarted)
        {
            return;
        }

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_state == IslandState.Calendar)
            {
                RenderCalendarPanel();
            }
        }));
    }

    private void RenderCalendarPanel()
    {
        if (CalendarItemsPanel is null)
        {
            return;
        }

        CalendarItemsPanel.Children.Clear();
        var enabled = _settings.CalendarEnabled;
        var next = enabled ? _calendar.Next : null;
        var denied = enabled && _calendar.Access == CalendarAccess.Denied;
        var checking = enabled && _calendar.Access == CalendarAccess.Unknown && next is null;

        CalendarCountText.Text = !enabled
            ? "off"
            : denied
                ? "unavailable"
                : next is null
                    ? checking ? "…" : "none"
                    : "next";

        var hasJoinable = false;
        if (CalendarEmptyState is not null)
        {
            CalendarEmptyState.Visibility = next is null ? Visibility.Visible : Visibility.Collapsed;
        }

        if (CalendarEmptyTitle is not null)
        {
            CalendarEmptyTitle.Text = !enabled
                ? "Calendar is off"
                : denied
                    ? "Calendar unavailable"
                    : checking ? "Checking calendar" : "Nothing scheduled";
        }

        if (CalendarEmptyHint is not null)
        {
            CalendarEmptyHint.Text = !enabled
                ? "Turn it on in Settings to see meetings."
                : denied
                    ? "Allow calendar access in Windows Settings."
                    : checking
                        ? "Reading the Windows calendar."
                        : "No appointments in the next 18 hours.";
        }

        if (next is null)
        {
            if (CalendarHintText is not null)
            {
                CalendarHintText.Visibility = Visibility.Collapsed;
            }

            return;
        }

        {
            var item = next;
            var row = new Border
            {
                Style = (Style)FindResource("ListCard"),
                Padding = new Thickness(8, 5, 6, 5)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var whenText = new TextBlock
            {
                Text = item.WhenLabel,
                FontFamily = (FontFamily)FindResource("UiText"),
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("SecondaryBrush")
            };
            System.Windows.Documents.Typography.SetNumeralAlignment(whenText, FontNumeralAlignment.Tabular);

            var when = new Border
            {
                Background = (Brush)FindResource("ChipBrush"),
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(8, 2, 8, 2),
                Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = whenText
            };
            AutomationProperties.SetName(when, item.WhenLabel);

            var title = new TextBlock
            {
                Text = item.Title,
                FontFamily = (FontFamily)FindResource("UiText"),
                FontSize = 12,
                Foreground = (Brush)FindResource("PrimaryBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };

            Grid.SetColumn(title, 1);
            grid.Children.Add(when);
            grid.Children.Add(title);

            if (!string.IsNullOrWhiteSpace(item.JoinUrl))
            {
                hasJoinable = true;
                var join = new Button
                {
                    Content = "Join",
                    Style = (Style)FindResource("AccentChipButton"),
                    Tag = item.JoinUrl,
                    Height = 26,
                    FontSize = 11,
                    MinWidth = 48,
                    Margin = new Thickness(8, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                join.Click += CalendarJoin_Click;
                AutomationProperties.SetName(join, $"Join {item.Title}");
                Grid.SetColumn(join, 2);
                grid.Children.Add(join);
            }

            row.Child = grid;
            CalendarItemsPanel.Children.Add(row);
        }

        if (CalendarHintText is not null)
        {
            CalendarHintText.Visibility = hasJoinable ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void CalendarJoin_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string url } || string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            DiagnosticsService.Write("Calendar", exception);
        }

        ClosePinned();
    }

    private void CalendarEntry_Click(object sender, RoutedEventArgs e) => ShowCalendar();

    // --------------------------------------------------------- panel scroll fade

    private void ListScroller_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer scroller || scroller.Tag is not Border fade)
        {
            return;
        }

        var hasOverflow = scroller.ComputedVerticalScrollBarVisibility == Visibility.Visible;
        var atEnd = scroller.VerticalOffset >= scroller.ScrollableHeight - 0.5;
        fade.Visibility = hasOverflow && !atEnd ? Visibility.Visible : Visibility.Collapsed;
    }

    // ----------------------------------------------------------------- prompt

    private void PromptEntry_Click(object sender, RoutedEventArgs e) => ShowPrompt(focusKeyboard: true);

    private void PromptSend_Click(object sender, RoutedEventArgs e) => _ = RunPromptAsync();

    private void PromptCancel_Click(object sender, RoutedEventArgs e) => CancelPrompt();

    private void PromptInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        _ = RunPromptAsync();
    }

    private void CancelPrompt()
    {
        if (_promptCancel is null || !_promptRequest.Cancel())
        {
            return;
        }

        _promptCancel.Cancel();
        if (PromptAnswer is not null)
        {
            PromptAnswer.Text = "Cancelled.";
        }

        EndPromptUi();
    }

    private async System.Threading.Tasks.Task RunPromptAsync()
    {
        if (PromptInput is null || !_promptRequest.TryStart(out var generation))
        {
            return;
        }

        var prompt = PromptInput.Text?.Trim() ?? string.Empty;
        if (prompt.Length == 0)
        {
            _promptRequest.Cancel();
            return;
        }

        _promptCancel?.Dispose();
        _promptCancel = new CancellationTokenSource();
        var token = _promptCancel.Token;

        BeginPromptUi();

        try
        {
            var result = await PromptService.RunAsync(prompt, token);

            // A cancelled or replaced request may still return; never let it
            // overwrite the answer that belongs to the current generation.
            if (Dispatcher.HasShutdownStarted || _promptRequest.Generation != generation)
            {
                return;
            }

            switch (result.State)
            {
                case PromptState.Cancelled:
                    _promptRequest.Cancel();
                    return;
                case PromptState.Failed:
                    if (!_promptRequest.Fail(generation, result.Text))
                    {
                        return;
                    }
                    break;
                default:
                    if (!_promptRequest.Complete(generation, result.Text))
                    {
                        return;
                    }
                    break;
            }

            if (PromptAnswer is not null)
            {
                PromptAnswer.Text = _promptRequest.LastText;
            }
        }
        catch (OperationCanceledException)
        {
            // CancelPrompt already updated the panel.
        }
        catch (Exception exception)
        {
            DiagnosticsService.Write("Prompt", exception);
            if (_promptRequest.Fail(generation, "The prompt failed.") && PromptAnswer is not null)
            {
                PromptAnswer.Text = _promptRequest.LastText;
            }
        }
        finally
        {
            if (_promptRequest.State != PromptState.Working)
            {
                EndPromptUi();
            }
        }
    }

    private void BeginPromptUi()
    {
        if (PromptSendButton is not null)
        {
            PromptSendButton.IsEnabled = false;
        }

        if (PromptCancelButton is not null)
        {
            PromptCancelButton.Visibility = Visibility.Visible;
        }

        if (PromptAnswer is not null)
        {
            PromptAnswer.Text = "Working…";
        }
    }

    private void EndPromptUi()
    {
        if (PromptSendButton is not null)
        {
            PromptSendButton.IsEnabled = true;
        }

        if (PromptCancelButton is not null)
        {
            PromptCancelButton.Visibility = Visibility.Collapsed;
        }
    }

    private TextBlock HintText(string text) => new()
    {
        Text = text,
        FontFamily = (FontFamily)FindResource("UiText"),
        FontSize = 12,
        Foreground = (Brush)FindResource("SecondaryBrush"),
        Margin = new Thickness(2, 8, 0, 0),
        TextWrapping = TextWrapping.Wrap
    };

    // --------------------------------------------------------- quick actions

    private void BuildQuickLaunch()
    {
        if (QuickLaunchPanel is null)
        {
            return;
        }

        // Three apps share the hero line with the focus pill; a fourth would
        // crowd the clock at the default 500 DIP width.
        var paths = _settings.QuickLaunch.Take(3).ToArray();
        var signature = string.Join('\u001F', paths);
        if (signature == _quickLaunchSignature)
        {
            return;
        }

        _quickLaunchSignature = signature;
        QuickLaunchPanel.Children.Clear();

        foreach (var path in paths)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (string.IsNullOrWhiteSpace(name))
            {
                name = "App";
            }

            var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var initials = words.Length >= 2
                ? $"{words[0][0]}{words[1][0]}"
                : name.Length >= 2 ? name[..2] : name;
            initials = new string(initials.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
            if (initials.Length == 0)
            {
                initials = "AP";
            }

            var button = new Button
            {
                Content = initials,
                Style = (Style)FindResource("HomeTileButton"),
                FontFamily = (FontFamily)FindResource("UiText"),
                Width = 44,
                Height = 44,
                FontSize = 12.5,
                Margin = new Thickness(8, 0, 0, 0),
                ToolTip = path
            };

            AutomationProperties.SetName(button, $"Launch {name}");
            var target = path;
            button.Click += (_, _) => LaunchQuickApp(target);
            QuickLaunchPanel.Children.Add(button);
        }
    }

    private static void LaunchQuickApp(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            DiagnosticsService.Write("QuickLaunch", exception);
        }
    }

    // ----------------------------------------------------------------- mirror

    private void ApplyMirrorDock()
    {
        if (IdleHomeDock is null || MirrorEntryButton is null)
        {
            return;
        }

        var enabled = _settings.MirrorEnabled;
        var children = IdleHomeDock.Children;

        if (enabled)
        {
            if (!children.Contains(MirrorEntryButton))
            {
                children.Add(MirrorEntryButton);
            }
        }
        else
        {
            children.Remove(MirrorEntryButton);
        }

        IdleHomeDock.Columns = enabled ? 5 : 4;

        if (!enabled && _state == IslandState.Mirror)
        {
            Transition(ResolveResting());
        }
    }

    private void MirrorEntry_Click(object sender, RoutedEventArgs e)
    {
        if (!_settings.MirrorEnabled)
        {
            return;
        }

        SetPinned(true);
        Transition(IslandState.Mirror);
    }

    private void MirrorClose_Click(object sender, RoutedEventArgs e) => ClosePinned();

    private void MirrorRetry_Click(object sender, RoutedEventArgs e)
    {
        PrepareMirrorPanel();
        _mirror.Retry(this);
    }

    private void MirrorOpenSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:privacy-webcam") { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            DiagnosticsService.Write("Mirror", exception);
        }
    }

    /// <summary>
    /// The camera is only live while the mirror is the visible, pinned panel.
    /// Hiding, pausing or leaving the panel releases it at once.
    /// </summary>
    private void SyncMirror()
    {
        var want = _settings.MirrorEnabled
            && _state == IslandState.Mirror
            && _pinned
            && Visibility == Visibility.Visible
            && !_paused;

        if (want == _mirrorWanted)
        {
            return;
        }

        if (want)
        {
            PrepareMirrorPanel();
        }
        else
        {
            _mirrorFrameShown = false;
            System.Threading.Interlocked.Exchange(ref _mirrorFramePending, 0);
            MirrorShutter.BeginAnimation(OpacityProperty, null);
            MirrorPreviewScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            MirrorPreviewScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            MirrorPreview.Source = null;
            MirrorPreview.Visibility = Visibility.Collapsed;
            MirrorLiveChip.Visibility = Visibility.Collapsed;
        }

        _mirrorWanted = want;
        _mirror.SetActive(this, want);
    }

    private void PrepareMirrorPanel()
    {
        _mirrorFrameShown = false;
        System.Threading.Interlocked.Exchange(ref _mirrorFramePending, 0);

        MirrorPreview.Source = null;
        MirrorPreview.Visibility = Visibility.Collapsed;
        MirrorPreviewScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        MirrorPreviewScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        MirrorPreviewScale.ScaleX = -1;
        MirrorPreviewScale.ScaleY = 1;

        MirrorShutter.BeginAnimation(OpacityProperty, null);
        MirrorShutter.Visibility = Visibility.Visible;
        MirrorShutter.Opacity = 1;
        MirrorLiveChip.Visibility = Visibility.Collapsed;

        ApplyMirrorState(MirrorState.Starting);
    }

    private void RenderMirrorPanel()
    {
        ApplyMirrorState(_mirror.State == MirrorState.Off ? MirrorState.Starting : _mirror.State);
    }

    private void OnMirrorStateChanged(MirrorState state)
    {
        if (Dispatcher.HasShutdownStarted)
        {
            return;
        }

        Dispatcher.BeginInvoke(new Action(() => ApplyMirrorState(state)));
    }

    private void ApplyMirrorState(MirrorState state)
    {
        if (MirrorShutter is null)
        {
            return;
        }

        StopMirrorPulse();
        MirrorActions.Visibility = Visibility.Collapsed;
        MirrorRetryButton.Visibility = Visibility.Collapsed;
        MirrorSettingsButton.Visibility = Visibility.Collapsed;

        switch (state)
        {
            case MirrorState.Starting:
                ShowMirrorShutter("Starting camera…", "Preview only. Nothing is recorded.", "\uE722");
                StartMirrorPulse();
                break;

            case MirrorState.Live:
                if (_mirrorFrameShown)
                {
                    MirrorLiveChip.Visibility = Visibility.Visible;
                }
                else
                {
                    ShowMirrorShutter("Connecting…", "Waiting for the first frame.", "\uE722");
                    StartMirrorPulse();
                }

                break;

            case MirrorState.Denied:
                ShowMirrorShutter(
                    "Camera access is off",
                    "Allow camera access in Windows Settings, then try again.",
                    "\uE72E");
                MirrorActions.Visibility = Visibility.Visible;
                MirrorRetryButton.Visibility = Visibility.Visible;
                MirrorSettingsButton.Visibility = Visibility.Visible;
                break;

            case MirrorState.NoCamera:
                ShowMirrorShutter(
                    "No camera found",
                    "Connect a camera and try again.",
                    "\uE722");
                MirrorActions.Visibility = Visibility.Visible;
                MirrorRetryButton.Visibility = Visibility.Visible;
                break;

            case MirrorState.Failed:
                ShowMirrorShutter(
                    "Camera unavailable",
                    "Close Mirror and try again, or check the camera privacy setting.",
                    "\uE7BA");
                MirrorActions.Visibility = Visibility.Visible;
                MirrorRetryButton.Visibility = Visibility.Visible;
                MirrorSettingsButton.Visibility = Visibility.Visible;
                break;
        }
    }

    private void ShowMirrorShutter(string title, string hint, string glyph)
    {
        _mirrorFrameShown = false;
        MirrorPreview.Visibility = Visibility.Collapsed;
        MirrorLiveChip.Visibility = Visibility.Collapsed;
        MirrorStatusGlyph.Text = glyph;
        MirrorStatusTitle.Text = title;
        MirrorStatusHint.Text = hint;

        MirrorShutter.BeginAnimation(OpacityProperty, null);
        MirrorShutter.Visibility = Visibility.Visible;
        MirrorShutter.Opacity = 1;
    }

    private void StartMirrorPulse()
    {
        if (ReducedMotion)
        {
            return;
        }

        var pulse = new DoubleAnimation(1, 0.35, TimeSpan.FromMilliseconds(850))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
        };

        MirrorStatusGlyph.BeginAnimation(OpacityProperty, pulse);
    }

    private void StopMirrorPulse()
    {
        MirrorStatusGlyph.BeginAnimation(OpacityProperty, null);
        MirrorStatusGlyph.Opacity = 1;
    }

    private void OnMirrorFrameReady()
    {
        if (Dispatcher.HasShutdownStarted)
        {
            return;
        }

        // Coalesce frames: one pending paint is enough; the newest buffer wins.
        if (System.Threading.Interlocked.Exchange(ref _mirrorFramePending, 1) == 1)
        {
            return;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(PaintMirrorFrame));
    }

    private void PaintMirrorFrame()
    {
        System.Threading.Interlocked.Exchange(ref _mirrorFramePending, 0);

        if (_state != IslandState.Mirror || !_mirrorWanted)
        {
            return;
        }

        var format = _mirror.Format;
        if (format is null)
        {
            return;
        }

        if (MirrorPreview.Source is not WriteableBitmap bitmap
            || bitmap.PixelWidth != format.Width
            || bitmap.PixelHeight != format.Height)
        {
            bitmap = new WriteableBitmap(
                format.Width,
                format.Height,
                96,
                96,
                PixelFormats.Bgra32,
                null);
            MirrorPreview.Source = bitmap;
        }

        if (!_mirror.CopyLatestFrame(bitmap))
        {
            return;
        }

        MirrorPreview.Visibility = Visibility.Visible;

        if (!_mirrorFrameShown)
        {
            RevealMirrorFrame();
        }
    }

    private void RevealMirrorFrame()
    {
        _mirrorFrameShown = true;
        StopMirrorPulse();
        MirrorLiveChip.Visibility = Visibility.Visible;

        if (ReducedMotion)
        {
            MirrorShutter.BeginAnimation(OpacityProperty, null);
            MirrorShutter.Opacity = 0;
            MirrorShutter.Visibility = Visibility.Collapsed;
            MirrorPreviewScale.ScaleX = -1;
            MirrorPreviewScale.ScaleY = 1;
            return;
        }

        // The shell has already sprung open; the shutter lifts and the frame
        // settles from a 4.5% push-in, so the camera start reads as a reveal.
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(440))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        };

        fade.Completed += (_, _) =>
        {
            MirrorShutter.BeginAnimation(OpacityProperty, null);
            MirrorShutter.Opacity = 0;
            MirrorShutter.Visibility = Visibility.Collapsed;
        };

        MirrorShutter.BeginAnimation(OpacityProperty, fade);

        var settleX = new DoubleAnimation(-1.045, -1, TimeSpan.FromMilliseconds(680))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        };
        var settleY = new DoubleAnimation(1.045, 1, TimeSpan.FromMilliseconds(680))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        };

        settleX.Completed += (_, _) =>
        {
            MirrorPreviewScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            MirrorPreviewScale.ScaleX = -1;
        };
        settleY.Completed += (_, _) =>
        {
            MirrorPreviewScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            MirrorPreviewScale.ScaleY = 1;
        };

        MirrorPreviewScale.BeginAnimation(ScaleTransform.ScaleXProperty, settleX);
        MirrorPreviewScale.BeginAnimation(ScaleTransform.ScaleYProperty, settleY);
    }

    // ---------------------------------------------------------- live activity

    /// <summary>
    /// A short, self-closing activity card (download, call, API event). It never
    /// replaces a notification peek and it clears itself after its duration.
    /// </summary>
    private void ShowLiveActivity(
        string title,
        string body,
        string glyph,
        double? fraction = null,
        TimeSpan? duration = null)
    {
        if (_paused || (IsOverlay(_state) && _state != IslandState.LiveActivity))
        {
            return;
        }

        _liveActivityTitle = title;
        _liveActivityBody = body;
        _liveActivityGlyph = glyph;
        _liveActivityFraction = fraction;

        PushOverlay(IslandState.LiveActivity);
        _liveActivityCountdown.Interval = duration ?? LiveActivityVisibleDuration;
        _liveActivityCountdown.Stop();
        _liveActivityCountdown.Start();
    }

    private void RenderLiveActivityPanel()
    {
        LiveActivityTitle.Text = _liveActivityTitle;
        LiveActivityBody.Text = _liveActivityBody;
        LiveActivityGlyph.Text = string.IsNullOrEmpty(_liveActivityGlyph) ? "\uE896" : _liveActivityGlyph;

        if (_liveActivityFraction is not { } fraction)
        {
            LiveActivityTrack.Visibility = Visibility.Collapsed;
            LiveActivityFill.Width = 0;
            return;
        }

        LiveActivityTrack.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            var width = LiveActivityTrack.ActualWidth;
            LiveActivityFill.Width = Math.Max(0, width * Math.Clamp(fraction, 0, 1));
        }), DispatcherPriority.Loaded);
    }

    private void OnTransferProgress(TransferInfo info)
    {
        if (Dispatcher.HasShutdownStarted)
        {
            return;
        }

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_settings.DownloadsEnabled || _paused)
            {
                return;
            }

            ShowLiveActivity(
                "Downloading",
                $"{info.Name} · {FormatBytes(info.Bytes)} · {FormatRate(info.BytesPerSecond)}",
                "\uE896",
                null,
                TimeSpan.FromSeconds(2.5));
        }));
    }

    private void OnDownloadArrived(DownloadInfo info)
    {
        if (Dispatcher.HasShutdownStarted)
        {
            return;
        }

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_settings.DownloadsEnabled || _paused)
            {
                return;
            }

            ShowLiveActivity(
                "Download ready",
                $"{info.Name} · {FormatBytes(info.Bytes)}",
                "\uE74E",
                null,
                TimeSpan.FromSeconds(5));

            FeedbackService.Notify();
        }));
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024)
        {
            return $"{bytes / (1024.0 * 1024 * 1024):0.0} GB";
        }

        if (bytes >= 1024 * 1024)
        {
            return $"{bytes / (1024.0 * 1024):0.0} MB";
        }

        return bytes >= 1024 ? $"{bytes / 1024.0:0} KB" : $"{bytes} B";
    }

    // ------------------------------------------------------------- navigation

    /// <summary>
    /// Shift + wheel cycles activities. With a panel pinned it walks the pinned
    /// panels; otherwise it walks the real resting activities that currently
    /// exist (idle, media, focus), so the gesture never lands on an empty state.
    /// </summary>
    private void CycleActivity(int direction)
    {
        if (!_settings.SwipeNavigationEnabled || _paused)
        {
            return;
        }

        var panels = new List<IslandState> { IslandState.Calendar };

        if (_settings.ShelfEnabled)
        {
            panels.Add(IslandState.Shelf);
        }

        panels.Add(IslandState.Notes);

        if (_settings.MirrorEnabled)
        {
            panels.Add(IslandState.Mirror);
        }

        if (_pinned && panels.Contains(_state))
        {
            var index = panels.IndexOf(_state);
            Transition(panels[(index + direction + panels.Count) % panels.Count]);
            _lastPresence = DateTime.UtcNow;
            return;
        }

        var activities = new List<IslandState> { IslandState.Idle };
        if (_settings.MediaEnabled && _currentMedia?.HasMedia == true)
        {
            activities.Add(IslandState.CompactMedia);
        }

        if (_settings.FocusEnabled && _focusTimer.Status != FocusTimerRunState.Stopped)
        {
            activities.Add(IslandState.CompactFocus);
        }

        if (activities.Count <= 1)
        {
            return;
        }

        var current = activities.IndexOf(_state);
        if (current < 0)
        {
            current = 0;
        }

        Transition(activities[(current + direction + activities.Count) % activities.Count]);
        _lastPresence = DateTime.UtcNow;
    }

    // --------------------------------------------------------------- public API

    public bool IsPaused => _paused;

    /// <summary>
    /// Routes a pause request to the app so every shell follows; the second
    /// launch path resumes process-wide rather than only this window.
    /// </summary>
    public Action<bool>? PauseRequested { get; set; }

    public void SetPaused(bool paused)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => SetPaused(paused)));
            return;
        }

        if (_paused == paused)
        {
            return;
        }

        _paused = paused;
        SetPinned(false);
        _hoverLeave.Stop();
        _lastPresence = DateTime.UtcNow;

        if (paused)
        {
            _splash.Stop();
            _notificationCountdown.Stop();
            _notificationQueue.Clear();
            _pendingNotification = null;
            _overlayReturnState = null;
            CancelPrompt();
            StopWaveform();
            HideIsland();
        }
        else
        {
            ShowIsland();
            Transition(ResolveResting());
        }
    }

    /// <summary>
    /// Second-launch / tray entry point: reveal the island and pin it on whatever
    /// activity it is currently showing. Unlike <see cref="ToggleIsland"/> this
    /// always shows, so re-running the exe never hides an overlay the user wanted.
    /// </summary>
    public void ShowFromTray()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(ShowFromTray));
            return;
        }

        if (_paused)
        {
            if (PauseRequested is { } request)
            {
                request(false);
            }
            else
            {
                SetPaused(false);
            }

            return;
        }

        ShowIsland();

        if (IsOverlay(_state))
        {
            DismissActiveOverlay();
        }

        PinCurrent(focusKeyboard: true);
    }

    /// <summary>
    /// Tray and global-hotkey entry point. The first press opens and pins the
    /// current activity; the next press closes the pin and recedes.
    /// </summary>
    public void ToggleIsland()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(ToggleIsland));
            return;
        }

        if (_paused)
        {
            return;
        }

        _lastPresence = DateTime.UtcNow;

        if (Visibility != Visibility.Visible)
        {
            ShowIsland();
        }

        // A transient peek must not swallow the press: drop it, then treat the
        // hotkey like any other toggle on the activity underneath.
        if (IsOverlay(_state))
        {
            DismissActiveOverlay();
        }

        if (_pinned)
        {
            ClosePinned();
            return;
        }

        PinCurrent(focusKeyboard: true);
    }

    public void StartFocusTimer(TimeSpan duration)
        => _focusTimer.Start(duration);

    public void ToggleFocusTimer()
        => _focusTimer.Toggle();

    public void StopFocusTimer()
        => _focusTimer.Reset();

    public void ExpandNowPlaying()
    {
        if (_currentMedia?.HasMedia == true && _state != IslandState.ExpandedMedia)
        {
            _splash.Stop();
            Transition(IslandState.ExpandedMedia);

            // Without this, a player opened from the tray would stay open forever
            // when the pointer is not over the island (no MouseLeave can fire).
            // A pinned panel is already held open, so it needs no timer.
            if (!_pinned)
            {
                _splashActive = true;
                _splash.Start();
            }
        }
    }

    public void ShowShelf(bool focusKeyboard = false)
    {
        if (!_settings.ShelfEnabled)
        {
            return;
        }

        SetPinned(true);
        Transition(IslandState.Shelf);
        FocusKeyboardIf(focusKeyboard);
    }

    public void ShowNotes(bool focusKeyboard = false)
    {
        SetPinned(true);
        Transition(IslandState.Notes);
        if (focusKeyboard)
        {
            SetKeyboardFocusMode(true);
            Dispatcher.BeginInvoke(new Action(() => NoteInput.Focus()));
        }
    }

    public void ShowCalendar()
    {
        if (!_settings.CalendarEnabled)
        {
            return;
        }

        SetPinned(true);
        Transition(IslandState.Calendar);
        _ = _calendar.RefreshAsync();
    }

    public void ShowPrompt(bool focusKeyboard = false)
    {
        if (!_settings.PromptEnabled)
        {
            return;
        }

        SetPinned(true);
        RefreshPromptPrivacy();
        Transition(IslandState.Prompt);
        if (focusKeyboard)
        {
            SetKeyboardFocusMode(true);
            Dispatcher.BeginInvoke(new Action(() => PromptInput.Focus()));
        }
    }

    private void RefreshPromptPrivacy()
    {
        if (PromptPrivacyText is null)
        {
            return;
        }

        PromptPrivacyText.Text = _settings.GroqEnabled
            ? "Sent to Groq"
            : "Runs on your own machine";
    }

    private void FocusKeyboardIf(bool focusKeyboard)
    {
        if (focusKeyboard)
        {
            SetKeyboardFocusMode(true);
        }
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _heartbeat.Stop();
        _timeline.Stop();
        _hoverLeave.Stop();
        _splash.Stop();
        _notificationCountdown.Stop();
        _chargingCountdown.Stop();
        _volumeCountdown.Stop();
        _brightnessCountdown.Stop();
        _focusCompleteCountdown.Stop();
        _waveform.Stop();
        _liveActivityCountdown.Stop();

        // A closed shell must not leave its claim on the shared WASAPI loopback
        // session; the last shell to close releases the capture.
        if (_captureWanted)
        {
            _captureWanted = false;
            _visualizer.Stop();
        }

        _mirror.SetActive(this, false);

        _promptCancel?.Cancel();
        _promptCancel?.Dispose();
        _promptCancel = null;

        if (_hotkeyRegistered && _windowHandle != IntPtr.Zero)
        {
            NativeMethods.UnregisterHotKey(_windowHandle, HotKeyId);
            _hotkeyRegistered = false;
        }

        UninstallMouseHook();

        if (_displayEventsHooked)
        {
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged;
            _displayEventsHooked = false;
        }

        SettingsService.Changed -= OnSettingsChanged;
        SettingsService.Previewed -= OnSettingsPreview;
        SettingsService.PreviewEnded -= OnSettingsPreviewEnded;

        _media.CurrentChanged -= OnMediaChanged;
        _media.TimelineChanged -= OnTimelineChanged;
        _notifications.Arrived -= OnNotificationArrived;
        _focusTimer.StateChanged -= OnFocusTimerStateChanged;
        _focusTimer.ElapsedChanged -= OnFocusTimerElapsed;
        _audio.VolumeChanged -= OnVolumeChanged;
        _brightness.BrightnessChanged -= OnBrightnessChanged;
        _shelf.Changed -= OnShelfChanged;
        _mirror.StateChanged -= OnMirrorStateChanged;
        _mirror.FrameReady -= OnMirrorFrameReady;
        _notes.Changed -= OnNotesChanged;
        _calendar.Changed -= OnCalendarChanged;
        _downloads.FileArrived -= OnDownloadArrived;
        _downloads.TransferProgress -= OnTransferProgress;
        _localApi.ActivityReceived -= OnApiActivity;
        _localApi.ShelfRequested -= OnApiShelf;
        _localApi.PanelRequested -= OnApiPanel;
        _localApi.MediaCommandRequested -= OnApiMedia;
        _localApi.FocusRequested -= OnApiFocus;

        // Shared services outlive an individual shell: in all-monitor mode every
        // screen has a window, and closing one must not stop media, toasts or the
        // local API for the others. App.OnExit shuts them down once.
    }
}
