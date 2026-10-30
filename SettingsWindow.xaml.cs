using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using TolkIsland.Services;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Clipboard = System.Windows.Clipboard;
using Color = System.Windows.Media.Color;
using Control = System.Windows.Controls.Control;
using ColorConverter = System.Windows.Media.ColorConverter;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Point = System.Windows.Point;
using ScrollBar = System.Windows.Controls.Primitives.ScrollBar;
using SystemColors = System.Windows.SystemColors;
using TabControl = System.Windows.Controls.TabControl;
using TextBox = System.Windows.Controls.TextBox;
using WinForms = System.Windows.Forms;

namespace TolkIsland;

public partial class SettingsWindow : Window
{
    private enum DialogChoice
    {
        Primary,
        Secondary,
        Cancel
    }

    private const int WmNcHitTest = 0x0084;
    private const double ShellInset = 20;
    private const double ResizeGrip = 7;

    private static readonly string[] Hotkeys =
    {
        "Ctrl+Alt+I",
        "Ctrl+Shift+Space",
        "Alt+Shift+I"
    };

    private readonly bool _isFirstRun;
    private bool _loading = true;
    private bool _dirty;
    private bool _allowClose;
    private bool _closePromptOpen;
    private bool _removeLicense;
    private bool _removeGroqKey;
    private bool _compact;
    private bool _short;
    private bool _dialogEscReturnsCancel = true;
    private TaskCompletionSource<DialogChoice>? _dialog;
    private IInputElement? _focusBeforeDialog;
    private ColumnDefinition? _railColumn;
    private FrameworkElement? _railFooter;
    private FrameworkElement? _railLogo;
    private FrameworkElement? _railScroll;
    private FrameworkElement? _contentHost;
    private FrameworkElement? _contentFade;
    private FrameworkElement? _moreHint;
    private Grid? _railInner;
    private ScrollViewer? _activeScroller;
    private readonly Dictionary<string, object?> _normalChrome = new();

    private static readonly string[] ChromeKeys =
    {
        "PrimaryBrush", "SecondaryBrush", "TertiaryBrush", "NavBrush",
        "DangerBrush", "SuccessBrush",
        "ScrollThumbBrush", "ScrollThumbHoverBrush", "ScrollThumbDragBrush",
        "ControlSurfaceBrush", "InputHoverBrush", "InputBorderBrush", "InputCaretBrush",
        "SecondaryInkBrush", "SecondaryHoverBrush", "SecondaryPressedBrush",
        "PrimaryActionSurfaceBrush", "PrimaryActionHoverBrush", "PrimaryActionPressedBrush",
        "PrimaryActionInkBrush",
        "IconButtonInkBrush", "IconButtonHoverBrush", "IconButtonHoverInkBrush",
        "IconButtonPressedBrush",
        "CloseHoverSurfaceBrush", "CloseHoverInkBrush", "ClosePressedSurfaceBrush",
        "DangerSurfaceBrush", "DangerHoverSurfaceBrush", "DangerPressedSurfaceBrush",
        "GhostSurfaceBrush", "GhostBorderBrush", "GhostInkBrush",
        "GhostHoverSurfaceBrush", "GhostHoverBorderBrush", "GhostPressedSurfaceBrush",
        "ToggleTrackBrush", "ToggleKnobBrush", "ToggleFocusSurfaceBrush",
        "SliderTrackBrush", "SliderThumbBrush",
        "NavHoverSurfaceBrush", "NavSelectedSurfaceBrush",
        "NavHoverInkBrush", "NavSelectedInkBrush",
        "RailSurfaceBrush", "RailDividerBrush", "RailLogoSurfaceBrush",
        "RailFooterSurfaceBrush", "RailFooterBorderBrush", "FooterBorderBrush",
        "ComboHoverSurfaceBrush", "ComboOpenSurfaceBrush", "ComboChevronBrush",
        "ComboPopupSurfaceBrush", "ComboItemInkBrush", "ComboItemHoverSurfaceBrush",
        "ComboItemSelectedSurfaceBrush",
        "DialogOverlayBrush", "DialogSurfaceBrush", "DialogBorderBrush",
        "ShellSurfaceBrush", "FooterSurfaceBrush", "ContentFadeSurfaceBrush",
        "CardBrush"
    };

    public SettingsWindow(bool isFirstRun = false)
    {
        InitializeComponent();

        _isFirstRun = isFirstRun;
        ApplyButton.Content = isFirstRun ? "Start using the island" : "Apply";
        VersionText.Text = $"Tolk Island {Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0"} for Windows";
        DataPathText.Text = SettingsService.DataDirectory;

        FitToWorkArea();
        PopulateMonitors();
        LoadSettings();
        HookDirtyTracking();

        ThemeCombo.SelectionChanged += Visual_Changed;
        AccentCombo.SelectionChanged += Visual_Changed;
        SkinCombo.SelectionChanged += Visual_Changed;
        HighContrastCheck.Checked += Visual_Changed;
        HighContrastCheck.Unchecked += Visual_Changed;
        ReducedMotionCheck.Checked += Visual_Changed;
        ReducedMotionCheck.Unchecked += Visual_Changed;
        LicenseKeyBox.TextChanged += LicenseKey_TextChanged;
        UpdateUrlBox.TextChanged += UpdateUrl_TextChanged;
        GroqKeyBox.PasswordChanged += GroqKey_PasswordChanged;
        SettingsTabs.SelectionChanged += SettingsTabs_SelectionChanged;
        SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
        SourceInitialized += SettingsWindow_SourceInitialized;

        foreach (var slider in new[] { HorizontalOffsetSlider, TopOffsetSlider, ScaleSlider, CornerSlider })
        {
            slider.PreviewMouseWheel += PlacementSlider_PreviewMouseWheel;
        }

        Closed += (_, _) =>
        {
            SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged;
            _dialog?.TrySetResult(DialogChoice.Cancel);
            SettingsService.EndPreview();
        };

        Loaded += (_, _) =>
        {
            SettingsTabs.SelectedIndex = 0;
            CacheTemplateParts();
            ApplyResponsiveLayout(ActualWidth, ActualHeight);
            UpdateValueLabels();
            UpdateOffsetAvailability();
            UpdateAccentChrome();
            ApplyChromeHighContrast();
            UpdatePreviewChrome();
            UpdateUpdateUrlState();
            RefreshPrivacyBadge();

            SetStatus(_isFirstRun
                ? "Set things up, then start using the island."
                : "Live changes preview on the island. Apply keeps them.");

            if (SettingsTabs.SelectedItem is TabItem selectedTab)
            {
                selectedTab.Focus();
            }

            Dispatcher.BeginInvoke(new Action(HookActiveScroller), DispatcherPriority.Background);
        };
    }

    private void HookDirtyTracking()
    {
        AddHandler(ToggleButton.CheckedEvent, new RoutedEventHandler(MarkDirty), true);
        AddHandler(ToggleButton.UncheckedEvent, new RoutedEventHandler(MarkDirty), true);
        AddHandler(TextBox.TextChangedEvent, new RoutedEventHandler(MarkDirty), true);
        AddHandler(Selector.SelectionChangedEvent, new RoutedEventHandler(MarkDirty), true);
        AddHandler(RangeBase.ValueChangedEvent, new RoutedEventHandler(MarkDirty), true);
    }

    private void MarkDirty(object? sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        if (e.OriginalSource is ScrollBar || e.OriginalSource is TabControl)
        {
            return;
        }

        if ((e.RoutedEvent == ToggleButton.CheckedEvent || e.RoutedEvent == ToggleButton.UncheckedEvent)
            && e.OriginalSource is not CheckBox)
        {
            return;
        }

        _dirty = true;
        DirtyDot.Visibility = Visibility.Visible;
        SetStatus("Live preview. Apply to keep.");
    }

    private void FitToWorkArea()
    {
        var area = SystemParameters.WorkArea;
        MaxWidth = Math.Max(MinWidth, area.Width - 24);
        MaxHeight = Math.Max(MinHeight, area.Height - 24);
        Width = Math.Min(Width, MaxWidth);
        Height = Math.Min(Height, MaxHeight);
    }

    private void CacheTemplateParts()
    {
        // Template-scoped names are not generated fields; resolve them here.
        _railColumn = SettingsTabs.Template?.FindName("RailColumn", SettingsTabs) as ColumnDefinition;
        _railFooter = SettingsTabs.Template?.FindName("PrivacyBadge", SettingsTabs) as FrameworkElement;
        _railLogo = SettingsTabs.Template?.FindName("RailLogo", SettingsTabs) as FrameworkElement;
        _railScroll = SettingsTabs.Template?.FindName("RailScroll", SettingsTabs) as FrameworkElement;
        _railInner = SettingsTabs.Template?.FindName("RailInner", SettingsTabs) as Grid;
        _contentHost = SettingsTabs.Template?.FindName("PART_SelectedContentHost", SettingsTabs) as FrameworkElement;
        _contentFade = SettingsTabs.Template?.FindName("ContentFade", SettingsTabs) as FrameworkElement;
        _moreHint = SettingsTabs.Template?.FindName("MoreHint", SettingsTabs) as FrameworkElement;

        if (_moreHint is Button more)
        {
            more.Click -= MoreHint_Click;
            more.Click += MoreHint_Click;
        }
    }

    private void MoreHint_Click(object sender, RoutedEventArgs e)
    {
        if (_activeScroller is null)
        {
            return;
        }

        var step = Math.Max(120, _activeScroller.ViewportHeight * 0.65);
        _activeScroller.ScrollToVerticalOffset(
            Math.Min(_activeScroller.ScrollableHeight, _activeScroller.VerticalOffset + step));
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        ApplyResponsiveLayout(ActualWidth, ActualHeight);
    }

    private void ApplyResponsiveLayout(double width, double height)
    {
        if (_railColumn is null)
        {
            CacheTemplateParts();
        }

        if (_railColumn is null)
        {
            return;
        }

        _compact = width < 900;

        _short = height < 560;

        _railColumn.Width = new GridLength(_compact ? 92 : 246);

        if (_railInner is not null)
        {
            _railInner.Margin = _compact
                ? new Thickness(10, _short ? 16 : 54, 10, _short ? 10 : 22)
                : new Thickness(16, _short ? 16 : 66, 16, _short ? 10 : 22);
        }

        if (_railLogo is not null)
        {
            _railLogo.Visibility = _short ? Visibility.Collapsed : Visibility.Visible;
        }

        if (_railScroll is not null)
        {
            _railScroll.Margin = new Thickness(0, _short ? 0 : 34, 0, 0);
        }

        if (_railFooter is not null)
        {
            _railFooter.Visibility = _compact || _short ? Visibility.Collapsed : Visibility.Visible;
        }

        if (_contentHost is not null)
        {
            _contentHost.Margin = _compact || _short
                ? new Thickness(26, _short ? 22 : 54, 26, 86)
                : new Thickness(34, 54, 30, 86);
        }

        if (_contentFade is not null)
        {
            _contentFade.Margin = _compact || _short
                ? new Thickness(26, 0, 26, 86)
                : new Thickness(34, 0, 30, 86);
        }

        Footer.Margin = new Thickness(_compact ? 92 : 246, 0, 0, 0);
        StatusRow.Margin = new Thickness(_compact ? 26 : 34, 0, 20, 0);

        foreach (var tab in SettingsTabs.Items.OfType<TabItem>())
        {
            var container = SettingsTabs.ItemContainerGenerator.ContainerFromItem(tab) as TabItem ?? tab;
            container.ApplyTemplate();

            if (container.Template?.FindName("HeaderText", container) is FrameworkElement header)
            {
                header.Visibility = _compact ? Visibility.Collapsed : Visibility.Visible;
            }

            if (container.Template?.FindName("GlyphColumn", container) is ColumnDefinition glyphColumn)
            {
                glyphColumn.Width = _compact
                    ? new GridLength(1, GridUnitType.Star)
                    : new GridLength(36);
            }

            if (container.Template?.FindName("Nav", container) is FrameworkElement nav)
            {
                nav.Margin = _compact ? new Thickness(4, 2, 4, 2) : new Thickness(8, 2, 8, 2);
            }
        }
    }

    internal void PrepareOffscreenLayout(double width, double height)
    {
        SettingsTabs.ApplyTemplate();
        CacheTemplateParts();
        ApplyResponsiveLayout(width, height);
    }

    private void PlacementSlider_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not Slider slider || !slider.IsEnabled || slider.TickFrequency <= 0)
        {
            return;
        }

        var delta = e.Delta > 0 ? slider.TickFrequency : -slider.TickFrequency;
        slider.Value = Math.Clamp(slider.Value + delta, slider.Minimum, slider.Maximum);
        e.Handled = true;
    }

    private void PopulateMonitors()
    {
        MonitorCombo.Items.Clear();
        foreach (var screen in WinForms.Screen.AllScreens)
        {
            MonitorCombo.Items.Add(new ComboBoxItem
            {
                Content = $"{screen.DeviceName}  ·  {screen.Bounds.Width}×{screen.Bounds.Height}",
                Tag = screen.DeviceName
            });
        }

        if (MonitorCombo.Items.Count > 0)
        {
            MonitorCombo.SelectedIndex = 0;
        }
    }

    private void LoadSettings()
    {
        _loading = true;
        _removeLicense = false;
        var settings = SettingsService.Current;

        StartWithWindowsCheck.IsChecked = StartupService.IsEnabled();
        IdleHideCheck.IsChecked = settings.IdleHide;
        QuietCheck.IsChecked = settings.QuietMode;
        ExpandModeCombo.SelectedIndex = (int)settings.ExpandMode;
        SoundsCheck.IsChecked = settings.NotificationSounds;
        HapticsCheck.IsChecked = settings.HapticsEnabled;
        LocalAiEndpointBox.Text = settings.LocalAiEndpoint;
        LocalAiModelBox.Text = settings.LocalAiModel;
        LocalAiError.Visibility = Visibility.Collapsed;
        GroqCheck.IsChecked = settings.GroqEnabled;
        GroqModelBox.Text = settings.GroqModel;
        GroqKeyBox.Password = string.Empty;
        GroqKeyError.Visibility = Visibility.Collapsed;
        _removeGroqKey = false;
        RefreshGroqKeyState();
        ApiBaseUrlBox.Text = IslandServices.Shared.LocalApi.BaseUrl;
        ApiTokenBox.Text = IslandServices.Shared.LocalApi.Token;
        ApiStatusText.Text = string.Empty;

        var hotkeyIndex = Array.IndexOf(Hotkeys, settings.Hotkey);
        HotkeyCombo.SelectedIndex = hotkeyIndex >= 0 ? hotkeyIndex : 0;

        MonitorModeCombo.SelectedIndex = (int)settings.MonitorMode;
        MonitorCombo.IsEnabled = settings.MonitorMode == MonitorMode.Specific;

        if (!string.IsNullOrWhiteSpace(settings.MonitorDeviceName))
        {
            for (var i = 0; i < MonitorCombo.Items.Count; i++)
            {
                if (MonitorCombo.Items[i] is ComboBoxItem item
                    && string.Equals(item.Tag as string, settings.MonitorDeviceName, StringComparison.OrdinalIgnoreCase))
                {
                    MonitorCombo.SelectedIndex = i;
                    break;
                }
            }
        }

        PositionCombo.SelectedIndex = (int)settings.PositionMode;
        HorizontalOffsetSlider.Value = settings.HorizontalOffset;
        TopOffsetSlider.Value = settings.TopOffset;
        ScaleSlider.Value = settings.Scale;
        CornerSlider.Value = settings.CornerRadius;

        ThemeCombo.SelectedIndex = (int)settings.Theme;
        SkinCombo.SelectedIndex = (int)settings.Skin;

        AccentCombo.SelectedIndex = 0;
        for (var i = 0; i < AccentCombo.Items.Count; i++)
        {
            if (AccentCombo.Items[i] is ComboBoxItem item
                && string.Equals(item.Tag as string, settings.AccentColor, StringComparison.OrdinalIgnoreCase))
            {
                AccentCombo.SelectedIndex = i;
                break;
            }
        }

        HighContrastCheck.IsChecked = settings.HighContrast;
        ReducedMotionCheck.IsChecked = settings.ReducedMotion;

        MediaCheck.IsChecked = settings.MediaEnabled;
        FocusCheck.IsChecked = settings.FocusEnabled;
        NotificationsCheck.IsChecked = settings.NotificationsEnabled;
        ChargingCheck.IsChecked = settings.ChargingEnabled;
        VolumeCheck.IsChecked = settings.VolumeEnabled;
        BrightnessCheck.IsChecked = settings.BrightnessEnabled;
        ShelfCheck.IsChecked = settings.ShelfEnabled;
        MediaFirstCheck.IsChecked = settings.MediaFirst;

        CalendarCheck.IsChecked = settings.CalendarEnabled;
        DownloadsCheck.IsChecked = settings.DownloadsEnabled;
        PromptCheck.IsChecked = settings.PromptEnabled;
        MirrorCheck.IsChecked = settings.MirrorEnabled;
        ReplyCheck.IsChecked = settings.NotificationReplyEnabled;
        SwipeCheck.IsChecked = settings.SwipeNavigationEnabled;

        UpdateCheckBox.IsChecked = settings.UpdateCheckEnabled;
        UpdateUrlBox.Text = settings.UpdateManifestUrl;
        QuickLaunchBox.Text = string.Join(Environment.NewLine, settings.QuickLaunch);
        NotificationSecondsSlider.Value = settings.NotificationVisibleSeconds;
        IdleSecondsSlider.Value = settings.IdleHideSeconds;

        BlockedAppsBox.Text = string.Join(Environment.NewLine, settings.BlockedApps);
        PriorityAppsBox.Text = string.Join(Environment.NewLine, settings.PriorityApps);

        _loading = false;
        UpdateValueLabels();
        UpdateOffsetAvailability();
        UpdateUpdateUrlState();
        UpdateLicenceUi();
        UpdateAccentChrome();
        UpdatePreviewChrome();
        UpdateSkinHint();
        RefreshPrivacyBadge();
    }

    private void RefreshPrivacyBadge()
    {
        var template = SettingsTabs?.Template;
        if (template?.FindName("PrivacyBadge", SettingsTabs) is not FrameworkElement badge
            || template.FindName("PrivacyBadgeGlyph", SettingsTabs) is not TextBlock glyph
            || template.FindName("PrivacyBadgeText", SettingsTabs) is not TextBlock text)
        {
            return;
        }

        var settings = SettingsService.Current;
        var online = settings.GroqEnabled
            || (settings.UpdateCheckEnabled && !string.IsNullOrWhiteSpace(settings.UpdateManifestUrl));

        glyph.Text = online ? "\uE774" : "\uE72E";
        text.Text = online ? "Some features go online" : "On-device only";
        badge.ToolTip = online
            ? "Groq prompts or update checks can leave this PC. Notes and history stay local."
            : "Every enabled feature runs on this PC. Nothing leaves it.";
    }

    private void UpdateSkinHint()
    {
        if (SkinHint is null)
        {
            return;
        }

        SkinHint.Text = SkinCombo?.SelectedIndex == 1
            ? "Scarlet and cobalt with a fine web canopy, and a small masked spider who hangs below the island. The accent follows the suit palette."
            : "Clean glass with one accent colour. Choice of tint, corner radius and size under Placement.";
    }

    private void UpdateValueLabels()
    {
        HorizontalOffsetText.Text = FormatOffset(HorizontalOffsetSlider.Value);
        TopOffsetText.Text = FormatOffset(TopOffsetSlider.Value);
        ScaleText.Text = $"{ScaleSlider.Value * 100:0}%";
        CornerText.Text = $"{CornerSlider.Value:0} px";
        NotificationSecondsText.Text = $"{NotificationSecondsSlider.Value:0} s";
        IdleSecondsText.Text = $"{IdleSecondsSlider.Value:0} s";
    }

    private static string FormatOffset(double value)
    {
        var cleaned = Math.Abs(value) < 0.5 ? 0 : Math.Round(value);
        return $"{cleaned:0} px";
    }

    private void UpdateOffsetAvailability()
    {
        if (PositionCombo.SelectedIndex < 0)
        {
            return;
        }

        var flush = PositionCombo.SelectedIndex == 0;
        TopOffsetSlider.IsEnabled = !flush;
        TopOffsetHint.Visibility = flush ? Visibility.Visible : Visibility.Collapsed;
        HorizontalOffsetSlider.IsEnabled = true;
    }

    private void MonitorMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MonitorCombo is null)
        {
            return;
        }

        MonitorCombo.IsEnabled = MonitorModeCombo.SelectedIndex == 2;
    }

    private void Position_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TopOffsetSlider is null)
        {
            return;
        }

        UpdateOffsetAvailability();
        PushPreview();
    }

    private void HorizontalOffset_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || HorizontalOffsetText is null)
        {
            return;
        }

        HorizontalOffsetText.Text = FormatOffset(e.NewValue);
        PushPreview();
    }

    private void TopOffset_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || TopOffsetText is null)
        {
            return;
        }

        TopOffsetText.Text = FormatOffset(e.NewValue);
        PushPreview();
    }

    private void Scale_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || ScaleText is null)
        {
            return;
        }

        ScaleText.Text = $"{e.NewValue * 100:0}%";
        PushPreview();
    }

    private void Corner_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || CornerText is null)
        {
            return;
        }

        CornerText.Text = $"{e.NewValue:0} px";
        PushPreview();
    }

    private void NotificationSeconds_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_loading && NotificationSecondsText is not null)
        {
            NotificationSecondsText.Text = $"{e.NewValue:0} s";
        }
    }

    private void IdleSeconds_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_loading && IdleSecondsText is not null)
        {
            IdleSecondsText.Text = $"{e.NewValue:0} s";
        }
    }

    private void Visual_Changed(object? sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        UpdateAccentChrome();
        UpdatePreviewChrome();
        PushPreview();
    }

    private void UpdateCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (UpdateUrlBox is null)
        {
            return;
        }

        UpdateUpdateUrlState();
    }

    private async void CheckForUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (UpdateCheckStatusText is null)
        {
            return;
        }

        UpdateCheckStatusText.Text = "Checking…";
        var result = await UpdateService.CheckAsync();
        UpdateCheckStatusText.Text = result.Status == UpdateCheckStatus.UpToDate
            ? $"Tolk Island {AppInfo.Version} is the newest version."
            : result.Message;

        if (result.Status != UpdateCheckStatus.UpdateAvailable || result.Update is not { } update)
        {
            return;
        }

        var notes = string.IsNullOrWhiteSpace(update.Notes) ? "No release notes." : update.Notes;
        var open = MessageWindow.Show(
            "Update available",
            $"Tolk Island {update.Version} is available.\n\n{notes}",
            "Open download page",
            "Not now");

        if (open && !UpdateService.OpenDownloadPage(update))
        {
            MessageWindow.Show(
                "Download page could not be opened",
                "The update manifest's download link was not a valid web address.",
                "OK");
        }
    }

    private void SettingsTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        Dispatcher.BeginInvoke(new Action(() =>
        {
            HookActiveScroller();
            PaintHighContrastCards(SystemParameters.HighContrast);
        }), DispatcherPriority.Background);
    }

    private void HookActiveScroller()
    {
        var scroller = (SettingsTabs.SelectedItem as TabItem)?.Content as ScrollViewer;

        if (!ReferenceEquals(scroller, _activeScroller))
        {
            if (_activeScroller is not null)
            {
                _activeScroller.ScrollChanged -= ActiveScroller_ScrollChanged;
            }

            _activeScroller = scroller;

            if (_activeScroller is not null)
            {
                _activeScroller.ScrollChanged += ActiveScroller_ScrollChanged;
            }
        }

        UpdateMoreHint();
    }

    private void ActiveScroller_ScrollChanged(object sender, ScrollChangedEventArgs e) => UpdateMoreHint();

    private void UpdateMoreHint()
    {
        if (_moreHint is null)
        {
            return;
        }

        var scroller = _activeScroller;
        var more = scroller is not null
            && scroller.ScrollableHeight > 1
            && scroller.VerticalOffset < scroller.ScrollableHeight - 0.5;

        _moreHint.Visibility = more ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateUrl_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (UpdateUrlHint is null)
        {
            return;
        }

        UpdateUpdateUrlState();
    }

    private static bool IsManifestUrl(string text)
        => Uri.TryCreate(text, UriKind.Absolute, out var uri)
           && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private void UpdateUpdateUrlState()
    {
        if (UpdateCheckBox is null || UpdateUrlBox is null)
        {
            return;
        }

        var on = UpdateCheckBox.IsChecked == true;
        var text = UpdateUrlBox.Text.Trim();
        var invalid = on && text.Length > 0 && !IsManifestUrl(text);

        UpdateUrlBox.IsEnabled = on;
        UpdateUrlError.Visibility = invalid ? Visibility.Visible : Visibility.Collapsed;
        UpdateUrlError.Text = "Use a full http or https address, such as https://example.com/tolk/update.json.";

        var showHint = !invalid && (!on || text.Length == 0);
        UpdateUrlHint.Visibility = showHint ? Visibility.Visible : Visibility.Collapsed;
        UpdateUrlHint.Text = on
            ? "For example, https://example.com/tolk/update.json."
            : "Turn on checks to edit this address.";

        if (invalid)
        {
            UpdateUrlBox.BorderBrush = (Brush)FindResource("DangerBrush");
        }
        else
        {
            UpdateUrlBox.ClearValue(Control.BorderBrushProperty);
        }
    }

    private void CopyApiUrl_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(ApiBaseUrlBox.Text);
            ApiStatusText.Text = "Base URL copied.";
        }
        catch
        {
            ApiStatusText.Text = "The clipboard is unavailable right now.";
        }
    }

    private void CopyApiToken_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(ApiTokenBox.Text);
            ApiStatusText.Text = "Access token copied.";
        }
        catch
        {
            ApiStatusText.Text = "The clipboard is unavailable right now.";
        }
    }

    private async void CheckApi_Click(object sender, RoutedEventArgs e)
    {
        var api = IslandServices.Shared.LocalApi;
        ApiStatusText.Text = "Checking...";

        if (!api.IsRunning)
        {
            ApiStatusText.Text = "The local API is not listening. Start the island, then try again.";
            return;
        }

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var body = await http.GetStringAsync(api.BaseUrl + "health");
            ApiStatusText.Text = "Healthy · " + body;
        }
        catch (Exception exception)
        {
            ApiStatusText.Text = "No answer from the API. " + exception.Message;
        }
    }

    private void PushPreview()
    {
        if (_loading
            || PositionCombo.SelectedIndex < 0
            || ThemeCombo.SelectedIndex < 0
            || AccentCombo.SelectedItem is not ComboBoxItem accent
            || accent.Tag is not string accentValue)
        {
            return;
        }

        SettingsService.Preview(new IslandPreview(
            (PositionMode)PositionCombo.SelectedIndex,
            HorizontalOffsetSlider.Value,
            TopOffsetSlider.Value,
            ScaleSlider.Value,
            CornerSlider.Value,
            (ThemeMode)ThemeCombo.SelectedIndex,
            accentValue,
            (SkinMode)Math.Max(0, SkinCombo.SelectedIndex),
            HighContrastCheck.IsChecked == true,
            ReducedMotionCheck.IsChecked == true));

        UpdateAccentChrome();
        UpdatePreviewChrome();
        MarkDirty(this, new RoutedEventArgs());
    }

    private void UpdateAccentChrome()
    {
        UpdateSkinHint();

        var spider = SkinCombo?.SelectedIndex == 1;
        if (AccentCombo is not null)
        {
            AccentCombo.IsEnabled = !spider;
            AccentCombo.ToolTip = spider ? "The Spider skin brings its own suit palette." : null;
        }

        if (SystemParameters.HighContrast)
        {
            Resources["AccentBrush"] = SystemColors.HighlightBrush;
            Resources["AccentBorderBrush"] = SystemColors.HighlightBrush;
            if (AmbientGlow is not null)
            {
                AmbientGlow.Background = null;
            }

            return;
        }

        var accent = SelectedAccentColor();
        Resources["AccentBrush"] = new SolidColorBrush(accent);
        Resources["AccentBorderBrush"] = new SolidColorBrush(
            Color.FromArgb(0x66, accent.R, accent.G, accent.B));

        if (AmbientGlow is null)
        {
            return;
        }

        var glow = new RadialGradientBrush
        {
            Center = new Point(0.82, 0.04),
            GradientOrigin = new Point(0.82, 0.04),
            RadiusX = 0.75,
            RadiusY = 0.6
        };
        glow.GradientStops.Add(new GradientStop(Color.FromArgb(0x24, accent.R, accent.G, accent.B), 0));
        glow.GradientStops.Add(new GradientStop(Color.FromArgb(0x0C, accent.R, accent.G, accent.B), 0.35));
        glow.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
        AmbientGlow.Background = glow;
    }

    private void OnSystemParametersChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (null or "" or nameof(SystemParameters.HighContrast)))
        {
            return;
        }

        Dispatcher.BeginInvoke(new Action(() =>
        {
            UpdateAccentChrome();
            ApplyChromeHighContrast();
            UpdatePreviewChrome();
        }));
    }

    private void ApplyChromeHighContrast()
    {
        var highContrast = SystemParameters.HighContrast;

        foreach (var key in ChromeKeys)
        {
            if (!_normalChrome.ContainsKey(key))
            {
                _normalChrome[key] = Resources[key];
            }

            if (highContrast)
            {
                Resources[key] = HighContrastChromeBrush(key);
            }
            else if (_normalChrome[key] is { } normal)
            {
                Resources[key] = normal;
            }
        }

        Foreground = (Brush)Resources["PrimaryBrush"];
        WindowShell.BorderBrush = highContrast ? SystemColors.WindowTextBrush : Brushes.Transparent;
        WindowShell.BorderThickness = new Thickness(highContrast ? 1 : 0);
        AmbientGlow.Visibility = highContrast ? Visibility.Collapsed : Visibility.Visible;
        PaintHighContrastCards(highContrast);
    }

    private static Brush HighContrastChromeBrush(string key) => key switch
    {
        "PrimaryActionSurfaceBrush" or "NavSelectedSurfaceBrush"
            or "ComboItemSelectedSurfaceBrush" or "CloseHoverSurfaceBrush"
            or "ClosePressedSurfaceBrush" => SystemColors.HighlightBrush,

        "PrimaryActionInkBrush" or "NavSelectedInkBrush" or "CloseHoverInkBrush"
            => SystemColors.HighlightTextBrush,

        "DangerHoverSurfaceBrush" or "DangerPressedSurfaceBrush"
            or "SecondaryHoverBrush" or "SecondaryPressedBrush"
            or "InputHoverBrush" or "ComboHoverSurfaceBrush" or "ComboOpenSurfaceBrush"
            or "NavHoverSurfaceBrush" or "ToggleFocusSurfaceBrush"
            or "ComboItemHoverSurfaceBrush" => SystemColors.ControlBrush,

        "IconButtonHoverBrush" or "GhostHoverSurfaceBrush" or "GhostPressedSurfaceBrush"
            or "DangerSurfaceBrush" or "GhostSurfaceBrush" or "RailLogoSurfaceBrush"
            or "RailFooterSurfaceBrush" or "ControlSurfaceBrush" or "CardBrush"
            => SystemColors.ControlBrush,

        "ToggleTrackBrush" or "SliderTrackBrush" or "IconButtonPressedBrush"
            => SystemColors.ControlDarkBrush,

        "InputBorderBrush" or "GhostBorderBrush" or "GhostHoverBorderBrush"
            or "RailDividerBrush" or "RailFooterBorderBrush" or "FooterBorderBrush"
            or "DialogBorderBrush" or "ToggleKnobBrush" or "SliderThumbBrush"
            or "ComboChevronBrush" => SystemColors.WindowTextBrush,

        "SecondaryInkBrush" or "IconButtonInkBrush" or "IconButtonHoverInkBrush"
            or "GhostInkBrush" or "ComboItemInkBrush" or "NavHoverInkBrush"
            => SystemColors.WindowTextBrush,

        "DialogOverlayBrush" => new SolidColorBrush(Color.FromArgb(0xB0, 0, 0, 0)),

        "DialogSurfaceBrush" or "ComboPopupSurfaceBrush" or "ShellSurfaceBrush"
            or "FooterSurfaceBrush" or "RailSurfaceBrush" => SystemColors.WindowBrush,

        "ContentFadeSurfaceBrush" => Brushes.Transparent,

        _ => SystemColors.WindowTextBrush
    };

    private void PaintHighContrastCards(bool highContrast)
    {
        if (SettingsTabs is null)
        {
            return;
        }

        var cardBrush = Resources["CardBrush"];
        Paint(SettingsTabs);

        void Paint(DependencyObject root)
        {
            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var index = 0; index < count; index++)
            {
                var child = VisualTreeHelper.GetChild(root, index);
                if (child is Border border && ReferenceEquals(border.Background, cardBrush))
                {
                    if (highContrast)
                    {
                        border.BorderBrush = SystemColors.WindowTextBrush;
                        border.BorderThickness = new Thickness(1);
                    }
                    else
                    {
                        border.ClearValue(Border.BorderBrushProperty);
                        border.ClearValue(Border.BorderThicknessProperty);
                    }
                }

                Paint(child);
            }
        }
    }

    private Color SelectedAccentColor()
    {
        if (SkinCombo?.SelectedIndex == 1)
        {
            return (Color)ColorConverter.ConvertFromString(SpiderSkin.RedDark);
        }

        if (AccentCombo?.SelectedItem is ComboBoxItem item && item.Tag is string value)
        {
            try
            {
                return (Color)ColorConverter.ConvertFromString(value);
            }
            catch
            {
            }
        }

        return Color.FromRgb(0x0A, 0x84, 0xFF);
    }

    private void UpdatePreviewChrome()
    {
        if (PreviewIsland is null || ThemeCombo is null)
        {
            return;
        }

        var theme = ThemeCombo.SelectedIndex < 0 ? ThemeMode.Dark : (ThemeMode)ThemeCombo.SelectedIndex;
        var light = theme == ThemeMode.Light || (theme == ThemeMode.System && IsSystemLightTheme());
        var contrast = HighContrastCheck?.IsChecked == true || SystemParameters.HighContrast;
        var spider = SkinCombo?.SelectedIndex == 1;

        if (contrast)
        {
            PreviewIsland.Background = SystemColors.WindowBrush;
            PreviewIsland.BorderBrush = SystemColors.WindowTextBrush;
            PreviewIsland.BorderThickness = new Thickness(1);
            PreviewClock.Foreground = SystemColors.WindowTextBrush;

            foreach (var bar in PreviewWave.Children.OfType<Border>())
            {
                bar.Background = SystemColors.HighlightBrush;
            }
        }
        else
        {
            PreviewIsland.Background = spider
                ? SpiderSkin.ShellFill(light)
                : new SolidColorBrush(light
                    ? Color.FromRgb(0xF8, 0xF8, 0xFB)
                    : Color.FromRgb(0x00, 0x00, 0x00));
            PreviewIsland.BorderBrush = spider
                ? SpiderSkin.Rim(light)
                : new SolidColorBrush(light
                    ? Color.FromArgb(0x4D, 0x00, 0x00, 0x00)
                    : Colors.Transparent);
            PreviewClock.Foreground = new SolidColorBrush(light
                ? Color.FromRgb(0x0B, 0x0B, 0x0D)
                : Colors.White);

            foreach (var bar in PreviewWave.Children.OfType<Border>())
            {
                bar.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
            }
        }

        var radius = CornerSlider?.Value ?? 30;
        var corner = Math.Min(33, Math.Max(0, radius));
        PreviewIsland.CornerRadius = new CornerRadius(corner);

        if (PreviewWeb is not null)
        {
            PreviewWeb.Visibility = spider && !contrast ? Visibility.Visible : Visibility.Collapsed;
            if (spider && !contrast)
            {
                PreviewWeb.Stroke = SpiderSkin.WebStroke(light);
                PreviewWeb.Data = SpiderSkin.BuildWeb(298, 64);
                PreviewWeb.Clip = new RectangleGeometry(new Rect(0, 0, 298, 64), corner, corner);
            }
        }

        var scale = ScaleSlider?.Value ?? 1;
        PreviewScale.ScaleX = scale;
        PreviewScale.ScaleY = scale;

        var surface = contrast
            ? "High contrast"
            : ThemeCombo.SelectedIndex switch
            {
                1 => "Light",
                2 => "Follow Windows",
                _ => "Dark"
            };
        var accentName = contrast
            ? "system colours"
            : spider
                ? "Spider suit palette"
                : ((AccentCombo?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Island blue") + " accent";
        PreviewCaption.Text = $"{surface} surface · {accentName} · {scale * 100:0}% size · {radius:0} px corners";
    }

    private static bool IsSystemLightTheme()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value != 0;
        }
        catch
        {
            return false;
        }
    }

    private void UpdateLicenceUi()
    {
        var settings = SettingsService.Current;
        var pro = LicenseService.IsPro;
        var wasLoading = _loading;
        _loading = true;

        try
        {
            LicenseErrorText.Visibility = Visibility.Collapsed;

            if (pro)
            {
                var suffix = settings.LicenseKey.Length >= 4
                    ? settings.LicenseKey[^4..]
                    : settings.LicenseKey;
                LicenseStatusText.Text = string.IsNullOrWhiteSpace(settings.LicenseKey)
                    ? "Pro is active on this PC."
                    : $"Pro is active with the key ending {suffix}.";
                LicenseKeyBox.Text = settings.LicenseKey;
                LicenseKeyBox.IsReadOnly = true;
                LicenseKeyHint.Text = "This key is active on this PC.";
                RemoveLicenseButton.Visibility = Visibility.Visible;
                return;
            }

            LicenseStatusText.Text = "Free plan. The island, media and notifications are included.";
            LicenseKeyBox.Text = settings.LicenseKey;
            LicenseKeyBox.IsReadOnly = false;
            LicenseKeyHint.Text = "Format TOLK-XXXX-XXXX-XXXX. Choose Apply to activate.";
            RemoveLicenseButton.Visibility = Visibility.Collapsed;
        }
        finally
        {
            _loading = wasLoading;
        }
    }

    private void LicenseKey_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || _removeLicense)
        {
            return;
        }

        if (LicenseKeyBox.Text.Trim().Length > 0)
        {
            LicenseStatusText.Text = LicenseService.IsPro
                ? "Pro is active on this PC."
                : "This key is checked when you apply.";
            LicenseKeyHint.Text = "Choose Apply to activate this key.";
            RemoveLicenseButton.Visibility = Visibility.Collapsed;
        }

        LicenseErrorText.Visibility = Visibility.Collapsed;
        MarkDirty(sender, e);
    }

    private void RemoveLicense_Click(object sender, RoutedEventArgs e)
    {
        _removeLicense = true;
        LicenseErrorText.Visibility = Visibility.Collapsed;
        LicenseStatusText.Text = "Pro will be removed when you apply.";
        LicenseKeyHint.Text = "Choose Apply to remove Pro, or enter a new key.";
        RemoveLicenseButton.Visibility = Visibility.Collapsed;
        LicenseKeyBox.IsReadOnly = false;
        LicenseKeyBox.Text = string.Empty;
        MarkDirty(sender, e);
    }

    private void GroqKey_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        if (GroqKeyBox.Password.Length > 0)
        {
            _removeGroqKey = false;
            GroqKeyError.Visibility = Visibility.Collapsed;
            RefreshGroqKeyState();
            GroqKeyHint.Text = "A new key will replace the saved one when you apply.";
        }
        else
        {
            RefreshGroqKeyState();
        }

        MarkDirty(sender, new RoutedEventArgs());
    }

    private void RemoveGroqKey_Click(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _removeGroqKey = true;
        _loading = true;
        try
        {
            GroqKeyBox.Password = string.Empty;
        }
        finally
        {
            _loading = false;
        }

        GroqKeyError.Visibility = Visibility.Collapsed;
        RefreshGroqKeyState();
        MarkDirty(sender, e);
    }

    private void RefreshGroqKeyState()
    {
        var hasKey = !string.IsNullOrEmpty(SettingsService.Current.GroqApiKeyProtected);
        GroqKeyHint.Text = _removeGroqKey
            ? "The saved key will be removed when you apply."
            : hasKey
                ? "A key is saved. Leave this blank to keep it, or type a new one to replace it."
                : "No key saved yet. Create one at console.groq.com/keys.";
        RemoveGroqKeyButton.IsEnabled = hasKey && !_removeGroqKey;
    }

    private bool TryStageLicense()
    {
        var key = LicenseKeyBox.Text.Trim().ToUpperInvariant();
        if (key.Length == 0)
        {
            return true;
        }

        if (LicenseService.IsValid(key))
        {
            return true;
        }

        LicenseErrorText.Text = "That key is not valid. Check the four groups and try again.";
        LicenseErrorText.Visibility = Visibility.Visible;
        LicenseStatusText.Text = "Key not recognized.";
        Announce(LicenseErrorText);
        SelectTab("Licence");
        LicenseKeyBox.Focus();
        LicenseKeyBox.SelectAll();
        return false;
    }

    private void SelectTab(string header)
    {
        var tab = SettingsTabs.Items
            .OfType<TabItem>()
            .FirstOrDefault(item => string.Equals(item.Header as string, header, StringComparison.Ordinal));

        if (tab is not null)
        {
            SettingsTabs.SelectedItem = tab;
        }
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        await ApplySettingsAsync(_isFirstRun);
    }

    private async Task<bool> ApplySettingsAsync(bool closeAfter)
    {
        if (!TryStageLicense())
        {
            return false;
        }

        var manifestUrl = UpdateUrlBox.Text.Trim();
        if (UpdateCheckBox.IsChecked == true && manifestUrl.Length > 0 && !IsManifestUrl(manifestUrl))
        {
            UpdateUpdateUrlState();
            SelectTab("Advanced");
            UpdateUrlBox.Focus();
            UpdateUrlBox.SelectAll();
            SetStatus("That manifest address is not valid.");
            return false;
        }

        var localAi = LocalAiEndpointBox.Text.Trim();
        if (localAi.Length > 0 && !localAi.Contains("://", StringComparison.Ordinal))
        {
            localAi = "http://" + localAi;
        }

        if (localAi.Length > 0 && !SafeUrl.IsLoopbackOrPrivate(localAi, out _))
        {
            SelectTab("Advanced");
            LocalAiError.Text = "Use an address on this PC or your own network, for example http://127.0.0.1:11434.";
            LocalAiError.Visibility = Visibility.Visible;
            LocalAiEndpointBox.Focus();
            LocalAiEndpointBox.SelectAll();
            SetStatus("That local AI address is not valid.");
            return false;
        }

        LocalAiError.Visibility = Visibility.Collapsed;
        var localAiModel = LocalAiModelBox.Text.Trim();

        var groqEnabled = GroqCheck.IsChecked == true;
        var groqInput = GroqKeyBox.Password.Trim();
        var storedGroqKey = SettingsService.Current.GroqApiKeyProtected;
        var hasGroqKey = groqInput.Length > 0
            || (!_removeGroqKey && !string.IsNullOrEmpty(storedGroqKey));
        string? encryptedGroqKey = null;
        GroqKeyError.Visibility = Visibility.Collapsed;

        if (groqInput.Length > 0)
        {
            if (groqInput.Length < 8 || groqInput.Length > 256 || groqInput.Any(char.IsWhiteSpace))
            {
                SelectTab("Advanced");
                GroqKeyError.Text = "That does not look like a Groq key. It starts with gsk_ and has no spaces.";
                GroqKeyError.Visibility = Visibility.Visible;
                GroqKeyBox.Focus();
                SetStatus("Check the Groq API key.");
                return false;
            }

            encryptedGroqKey = SecretProtection.Protect(groqInput);
            if (encryptedGroqKey.Length == 0)
            {
                SelectTab("Advanced");
                GroqKeyError.Text = "Windows could not encrypt this key. Try again.";
                GroqKeyError.Visibility = Visibility.Visible;
                GroqKeyBox.Focus();
                SetStatus("The Groq API key was not saved.");
                return false;
            }
        }

        if (groqEnabled && !hasGroqKey)
        {
            SelectTab("Advanced");
            GroqKeyError.Text = "Add a Groq API key, or turn Groq off.";
            GroqKeyError.Visibility = Visibility.Visible;
            GroqKeyBox.Focus();
            SetStatus("Groq needs an API key.");
            return false;
        }

        var groqModel = GroqModelBox.Text.Trim();

        var monitorMode = (MonitorMode)Math.Max(0, MonitorModeCombo.SelectedIndex);
        var positionMode = (PositionMode)Math.Max(0, PositionCombo.SelectedIndex);
        var theme = (ThemeMode)Math.Max(0, ThemeCombo.SelectedIndex);
        var hotkey = Hotkeys[Math.Max(0, HotkeyCombo.SelectedIndex)];
        var accent = (AccentCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "#FF0A84FF";
        var monitorDevice = (MonitorCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        var licenceKey = LicenseKeyBox.Text.Trim().ToUpperInvariant();

        var save = SettingsService.Update(settings =>
        {
            settings.MonitorMode = monitorMode;
            settings.MonitorDeviceName = monitorDevice;
            settings.PositionMode = positionMode;
            settings.HorizontalOffset = HorizontalOffsetSlider.Value;
            settings.TopOffset = TopOffsetSlider.Value;
            settings.Scale = ScaleSlider.Value;
            settings.CornerRadius = CornerSlider.Value;
            settings.Theme = theme;
            settings.Skin = (SkinMode)Math.Max(0, SkinCombo.SelectedIndex);
            settings.AccentColor = accent;
            settings.HighContrast = HighContrastCheck.IsChecked == true;
            settings.ReducedMotion = ReducedMotionCheck.IsChecked == true;
            settings.IdleHide = IdleHideCheck.IsChecked == true;
            settings.QuietMode = QuietCheck.IsChecked == true;
            settings.Hotkey = hotkey;
            settings.MediaEnabled = MediaCheck.IsChecked == true;
            settings.FocusEnabled = FocusCheck.IsChecked == true;
            settings.NotificationsEnabled = NotificationsCheck.IsChecked == true;
            settings.ChargingEnabled = ChargingCheck.IsChecked == true;
            settings.VolumeEnabled = VolumeCheck.IsChecked == true;
            settings.BrightnessEnabled = BrightnessCheck.IsChecked == true;
            settings.ShelfEnabled = ShelfCheck.IsChecked == true;
            settings.MediaFirst = MediaFirstCheck.IsChecked == true;
            settings.CalendarEnabled = CalendarCheck.IsChecked == true;
            settings.DownloadsEnabled = DownloadsCheck.IsChecked == true;
            settings.PromptEnabled = PromptCheck.IsChecked == true;
            settings.MirrorEnabled = MirrorCheck.IsChecked == true;
            settings.NotificationReplyEnabled = ReplyCheck.IsChecked == true;
            settings.SwipeNavigationEnabled = SwipeCheck.IsChecked == true;
            settings.UpdateCheckEnabled = UpdateCheckBox.IsChecked == true;
            settings.UpdateManifestUrl = UpdateUrlBox.Text.Trim();
            settings.QuickLaunch = SplitLines(QuickLaunchBox.Text);
            settings.ExpandMode = (ExpandMode)Math.Max(0, ExpandModeCombo.SelectedIndex);
            settings.NotificationSounds = SoundsCheck.IsChecked == true;
            settings.HapticsEnabled = HapticsCheck.IsChecked == true;
            settings.LocalAiEndpoint = localAi;
            settings.LocalAiModel = localAiModel.Length > 0 ? localAiModel : "llama3.2";
            settings.GroqEnabled = groqEnabled;
            settings.GroqModel = groqModel.Length > 0 ? groqModel : AppSettings.DefaultGroqModel;
            if (encryptedGroqKey is not null)
            {
                settings.GroqApiKeyProtected = encryptedGroqKey;
            }
            else if (_removeGroqKey)
            {
                settings.GroqApiKeyProtected = string.Empty;
            }
            settings.NotificationVisibleSeconds = (int)Math.Round(NotificationSecondsSlider.Value);
            settings.IdleHideSeconds = (int)Math.Round(IdleSecondsSlider.Value);
            settings.BlockedApps = SplitLines(BlockedAppsBox.Text);
            settings.PriorityApps = SplitLines(PriorityAppsBox.Text);
            settings.OnboardingCompleted = true;

            if (licenceKey.Length > 0)
            {
                settings.LicenseKey = licenceKey;
                settings.ProUnlocked = true;
            }
            else if (_removeLicense)
            {
                settings.LicenseKey = string.Empty;
                settings.ProUnlocked = false;
            }
        });

        _loading = true;
        try
        {
            UpdateLicenceUi();
        }
        finally
        {
            _loading = false;
        }

        UpdateUpdateUrlState();
        UpdateAccentChrome();
        UpdatePreviewChrome();

        if (!save.Ok)
        {
            SettingsService.EndPreview();
            SetStatus(save.Message);
            await ShowDialogAsync(
                "Settings were not saved",
                save.Message + "\n\nNothing was changed; the island is still using your previous settings. Check that the TolkIsland folder in LocalAppData is writable, then apply again.",
                "OK",
                null,
                null);
            return false;
        }

        _loading = true;
        try
        {
            _removeGroqKey = false;
            GroqKeyBox.Password = string.Empty;
            GroqKeyError.Visibility = Visibility.Collapsed;
            RefreshGroqKeyState();
        }
        finally
        {
            _loading = false;
        }

        _removeLicense = false;
        _dirty = false;
        DirtyDot.Visibility = Visibility.Collapsed;
        SetStatus("Saved. The island is using these settings.");

        if (StartWithWindowsCheck.IsChecked != StartupService.IsEnabled())
        {
            var wantsStartup = StartWithWindowsCheck.IsChecked == true;
            if (!StartupService.SetEnabled(wantsStartup))
            {
                _loading = true;
                try
                {
                    StartWithWindowsCheck.IsChecked = StartupService.IsEnabled();
                }
                finally
                {
                    _loading = false;
                }

                await ShowDialogAsync(
                    "Startup entry was not saved",
                    "Windows did not accept the startup entry, so the switch was turned back off. Your other settings were saved.",
                    "OK",
                    null,
                    null);
            }
        }

        if (closeAfter)
        {
            _allowClose = true;
            Close();
        }

        return true;
    }

    private static List<string> SplitLines(string text)
        => text
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private async void Cancel_Click(object sender, RoutedEventArgs e) => await RequestCloseAsync();

    private async void Close_Click(object sender, RoutedEventArgs e) => await RequestCloseAsync();

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose || !_dirty)
        {
            return;
        }

        e.Cancel = true;

        if (!_closePromptOpen && _dialog is null)
        {
            _ = RequestCloseAsync();
        }
    }

    private async Task RequestCloseAsync()
    {
        if (_dialog is not null || _closePromptOpen)
        {
            return;
        }

        if (!_dirty)
        {
            _allowClose = true;
            Close();
            return;
        }

        _closePromptOpen = true;
        var choice = await ShowDialogAsync(
            "Save your changes?",
            "The island is previewing changes that have not been applied yet.",
            "Save changes",
            "Discard",
            "Keep editing");
        _closePromptOpen = false;

        switch (choice)
        {
            case DialogChoice.Primary:
                if (await ApplySettingsAsync(closeAfter: false))
                {
                    _allowClose = true;
                    Close();
                }

                break;
            case DialogChoice.Secondary:
                _allowClose = true;
                Close();
                break;
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_dialog is not null)
        {
            if (e.Key == Key.Escape)
            {
                ResolveDialog(_dialogEscReturnsCancel ? DialogChoice.Cancel : DialogChoice.Primary);
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Tab)
            {
                MoveDialogFocus(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
                e.Handled = true;
            }

            return;
        }

        if (e.Key == Key.Escape)
        {
            if (FindAncestor<System.Windows.Controls.ComboBox>(Keyboard.FocusedElement as DependencyObject)
                is { IsDropDownOpen: true } combo)
            {
                combo.IsDropDownOpen = false;
                e.Handled = true;
                return;
            }

            _ = RequestCloseAsync();
            e.Handled = true;
        }
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private void MoveDialogFocus(bool backwards)
    {
        var buttons = new[] { DialogCancelButton, DialogSecondaryButton, DialogPrimaryButton }
            .Where(button => button.Visibility == Visibility.Visible)
            .ToArray();

        if (buttons.Length == 0)
        {
            return;
        }

        var current = Keyboard.FocusedElement as DependencyObject;
        var index = Array.FindIndex(buttons, button => button == current || button.IsKeyboardFocusWithin);
        var next = index < 0
            ? (backwards ? buttons.Length - 1 : 0)
            : (index + (backwards ? -1 : 1) + buttons.Length) % buttons.Length;

        buttons[next].Focus();
    }

    private Task<DialogChoice> ShowDialogAsync(
        string title,
        string message,
        string primaryText,
        string? secondaryText,
        string? cancelText,
        bool primaryIsDanger = false)
    {
        if (_dialog is not null)
        {
            return _dialog.Task;
        }

        _dialog = new TaskCompletionSource<DialogChoice>(TaskCreationOptions.RunContinuationsAsynchronously);
        _focusBeforeDialog = Keyboard.FocusedElement;
        _dialogEscReturnsCancel = cancelText is not null;

        DialogTitle.Text = title;
        DialogMessage.Text = message;
        DialogPrimaryButton.Content = primaryText;
        DialogPrimaryButton.Style = (Style)FindResource(primaryIsDanger ? "DangerButton" : "PrimaryButton");
        DialogSecondaryButton.Content = secondaryText ?? string.Empty;
        DialogSecondaryButton.Visibility = secondaryText is null ? Visibility.Collapsed : Visibility.Visible;
        DialogCancelButton.Content = cancelText ?? "Cancel";
        DialogCancelButton.Visibility = cancelText is null ? Visibility.Collapsed : Visibility.Visible;

        DialogOverlay.Visibility = Visibility.Visible;
        ApplyButton.IsDefault = false;

        if (primaryIsDanger && cancelText is not null)
        {
            DialogCancelButton.IsDefault = true;
            DialogCancelButton.Focus();
        }
        else
        {
            DialogPrimaryButton.IsDefault = true;
            DialogPrimaryButton.Focus();
        }

        return _dialog.Task;
    }

    private void ResolveDialog(DialogChoice choice)
    {
        var dialog = _dialog;
        if (dialog is null)
        {
            return;
        }

        _dialog = null;
        DialogOverlay.Visibility = Visibility.Collapsed;
        DialogPrimaryButton.IsDefault = false;
        DialogCancelButton.IsDefault = false;
        ApplyButton.IsDefault = true;

        if (_focusBeforeDialog is UIElement element && element.IsVisible)
        {
            element.Focus();
        }

        dialog.TrySetResult(choice);
    }

    private void DialogPrimary_Click(object sender, RoutedEventArgs e) => ResolveDialog(DialogChoice.Primary);

    private void DialogSecondary_Click(object sender, RoutedEventArgs e) => ResolveDialog(DialogChoice.Secondary);

    private void DialogCancel_Click(object sender, RoutedEventArgs e) => ResolveDialog(DialogChoice.Cancel);

    private void SettingsWindow_SourceInitialized(object? sender, EventArgs e)
    {
        if (PresentationSource.FromVisual(this) is HwndSource source)
        {
            source.AddHook(WindowHitTest);
        }
    }

    private IntPtr WindowHitTest(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmNcHitTest || WindowState != WindowState.Normal || !IsVisible)
        {
            return IntPtr.Zero;
        }

        var value = lParam.ToInt64();
        var screenPoint = new Point(
            (short)(value & 0xFFFF),
            (short)((value >> 16) & 0xFFFF));
        var point = PointFromScreen(screenPoint);

        var left = Math.Abs(point.X - ShellInset) <= ResizeGrip;
        var right = Math.Abs(point.X - (ActualWidth - ShellInset)) <= ResizeGrip;
        var top = Math.Abs(point.Y - ShellInset) <= ResizeGrip;
        var bottom = Math.Abs(point.Y - (ActualHeight - ShellInset)) <= ResizeGrip;

        var hit = 0;
        if (left && top)
        {
            hit = 13; // HTTOPLEFT
        }
        else if (right && top)
        {
            hit = 14; // HTTOPRIGHT
        }
        else if (left && bottom)
        {
            hit = 16; // HTBOTTOMLEFT
        }
        else if (right && bottom)
        {
            hit = 17; // HTBOTTOMRIGHT
        }
        else if (left)
        {
            hit = 10; // HTLEFT
        }
        else if (right)
        {
            hit = 11; // HTRIGHT
        }
        else if (top)
        {
            hit = 12; // HTTOP
        }
        else if (bottom)
        {
            hit = 15; // HTBOTTOM
        }

        if (hit == 0)
        {
            return IntPtr.Zero;
        }

        handled = true;
        return new IntPtr(hit);
    }

    private void DragArea_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private async void OpenDataFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(SettingsService.DataDirectory);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{SettingsService.DataDirectory}\"")
            {
                UseShellExecute = true
            });
        }
        catch
        {
            await ShowDialogAsync(
                "Folder could not be opened",
                "Windows could not open the local data folder. The path is shown above so you can open it manually.",
                "OK",
                null,
                null);
        }
    }

    private async void CopyDataPath_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(SettingsService.DataDirectory);
            SetStatus("Data folder path copied.");
        }
        catch
        {
            await ShowDialogAsync(
                "Clipboard unavailable",
                "The folder path could not be copied. Select the path above and copy it manually.",
                "OK",
                null,
                null);
        }
    }

    private async void Reset_Click(object sender, RoutedEventArgs e)
    {
        var choice = await ShowDialogAsync(
            "Reset all settings?",
            "Every preference, the Pro unlock, the licence key, the Groq API key and the Windows startup entry will be removed. Files in the shelf are not deleted.",
            "Reset settings",
            null,
            "Cancel",
            primaryIsDanger: true);

        if (choice != DialogChoice.Primary)
        {
            return;
        }

        var reset = SettingsService.Reset();
        if (!reset.Ok)
        {
            SetStatus(reset.Message);
            await ShowDialogAsync(
                "Settings were not reset",
                reset.Message + "\n\nNothing was changed; your current settings are still in effect.",
                "OK",
                null,
                null);
            return;
        }

        SettingsService.EndPreview();
        var startupCleared = StartupService.SetEnabled(false);
        _dirty = false;
        DirtyDot.Visibility = Visibility.Collapsed;
        LoadSettings();
        SetStatus(startupCleared
            ? "Settings reset."
            : "Settings reset, but the Windows startup entry could not be removed. See the diagnostics log.");
    }

    private void SetStatus(string message)
    {
        StatusText.Text = message;
        Announce(StatusText);
    }

    private static void Announce(FrameworkElement element)
    {
        if (!AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged))
        {
            return;
        }

        var peer = UIElementAutomationPeer.FromElement(element)
                   ?? UIElementAutomationPeer.CreatePeerForElement(element);
        peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }
}
