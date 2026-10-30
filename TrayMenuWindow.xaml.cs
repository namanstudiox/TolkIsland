using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using TolkIsland.Native;
using TolkIsland.Services;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using Forms = System.Windows.Forms;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Point = System.Windows.Point;
using SystemColors = System.Windows.SystemColors;

namespace TolkIsland;

public partial class TrayMenuWindow : Window
{
    private const double ShadowMargin = 14;
    private static TrayMenuWindow? _open;

    private bool _placed;

    public TrayMenuWindow(IReadOnlyList<TrayMenuItem> items)
    {
        InitializeComponent();
        ApplyAccent();
        ApplyHeader(items);
        ApplyHighContrastChrome();
        ItemsHost.ItemsSource = items;

        Loaded += (_, _) => PlaceAtCursor();
        ContentRendered += (_, _) => AnimateIn();
    }

    private void ApplyHeader(IReadOnlyList<TrayMenuItem> items)
    {
        var version = typeof(TrayMenuWindow).Assembly.GetName().Version;
        HeaderMeta.Text = version is null ? "Version 1.0.0" : $"Version {version.ToString(3)}";

        var paused = false;
        foreach (var item in items)
        {
            if (item.Label == "Pause island" && item.IsOn == true)
            {
                paused = true;
                break;
            }
        }

        HeaderState.Text = paused ? "Paused" : "Running";
        var stateColor = paused
            ? Color.FromRgb(0xFF, 0xB3, 0x40)
            : Color.FromRgb(0x30, 0xD1, 0x58);
        var stateBrush = new SolidColorBrush(stateColor);
        stateBrush.Freeze();
        StatusDot.Fill = stateBrush;
    }

    public static void ShowMenu(IReadOnlyList<TrayMenuItem> items)
    {
        if (_open is { } existing)
        {
            existing.Close();
            _open = null;
            return;
        }

        var menu = new TrayMenuWindow(items);
        _open = menu;
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(_open, menu))
            {
                _open = null;
            }
        };
        menu.Show();
    }

    private void ApplyAccent()
    {
        var settings = SettingsService.Current;
        var accent = ParseColor(SpiderSkin.AccentFor(settings.Skin, dark: true, settings.AccentColor))
                     ?? Color.FromRgb(0x0A, 0x84, 0xFF);

        var accentBrush = new SolidColorBrush(accent);
        accentBrush.Freeze();
        Resources["AccentBrush"] = accentBrush;

        var glow = new RadialGradientBrush
        {
            Center = new Point(0.86, 0.0),
            GradientOrigin = new Point(0.86, 0.0),
            RadiusX = 0.8,
            RadiusY = 0.65
        };
        glow.GradientStops.Add(new GradientStop(Color.FromArgb(0x1F, accent.R, accent.G, accent.B), 0.0));
        glow.GradientStops.Add(new GradientStop(Color.FromArgb(0x07, accent.R, accent.G, accent.B), 0.5));
        glow.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, accent.R, accent.G, accent.B), 1.0));
        glow.Freeze();
        Glow.Background = glow;
    }

    private void ApplyHighContrastChrome()
    {
        if (!SystemParameters.HighContrast)
        {
            return;
        }

        Resources["ItemForegroundBrush"] = SystemColors.WindowTextBrush;
        Resources["GlyphBrush"] = SystemColors.WindowTextBrush;
        Resources["ShadowSurfaceBrush"] = SystemColors.WindowBrush;
        Resources["SurfaceBorderBrush"] = SystemColors.WindowTextBrush;
        Resources["TraySurfaceBrush"] = SystemColors.WindowBrush;
        Resources["HoverSurfaceBrush"] = SystemColors.ControlBrush;
        Resources["PressedSurfaceBrush"] = SystemColors.ControlDarkBrush;
        Resources["FocusRingBrush"] = SystemColors.WindowTextBrush;
        Resources["KeycapSurfaceBrush"] = SystemColors.ControlBrush;
        Resources["KeycapBorderBrush"] = SystemColors.WindowTextBrush;
        Resources["KeycapInkBrush"] = SystemColors.WindowTextBrush;
        Resources["DividerBrush"] = SystemColors.WindowTextBrush;
        Resources["SwitchTrackBrush"] = SystemColors.ControlDarkBrush;
        Resources["SwitchTrackBorderBrush"] = SystemColors.WindowTextBrush;
        Resources["SwitchKnobBrush"] = SystemColors.WindowTextBrush;
        Resources["DangerInkBrush"] = SystemColors.WindowTextBrush;
        Resources["DangerGlyphBrush"] = SystemColors.WindowTextBrush;
        Resources["HeaderPlateSurfaceBrush"] = SystemColors.ControlBrush;
        Resources["HeaderPlateBorderBrush"] = SystemColors.WindowTextBrush;
        Resources["AccentBrush"] = SystemColors.HighlightBrush;

        Glow.Background = null;
        StatusDot.Fill = SystemColors.HighlightBrush;
    }

    private static Color? ParseColor(string? value)
    {
        try
        {
            return string.IsNullOrWhiteSpace(value)
                ? null
                : ColorConverter.ConvertFromString(value) as Color?;
        }
        catch
        {
            return null;
        }
    }

    private void PlaceAtCursor()
    {
        try
        {
            var cursor = Forms.Cursor.Position;
            var screen = Forms.Screen.FromPoint(cursor);
            var work = screen.WorkingArea;
            var dpi = GetScreenDpi(work);

            ItemsScroll.MaxHeight = Math.Max(220, work.Height / dpi - 190);
            UpdateLayout();

            var width = ActualWidth * dpi;
            var height = ActualHeight * dpi;

            var x = cursor.X + (ShadowMargin + 6) * dpi - width;
            var y = cursor.Y + (ShadowMargin - 4) * dpi - height;

            var left = work.Left + 8 * dpi;
            var top = work.Top + 8 * dpi;
            var right = work.Right - 8 * dpi;
            var bottom = work.Bottom - 8 * dpi;

            x = Math.Min(x, right - width);
            x = Math.Max(x, left);

            if (y < top)
            {
                y = cursor.Y + (4 - ShadowMargin) * dpi;
            }

            y = Math.Min(y, bottom - height);
            y = Math.Max(y, top);

            var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            NativeMethods.SetWindowPos(
                handle,
                new IntPtr(-1),
                (int)Math.Round(x),
                (int)Math.Round(y),
                0,
                0,
                NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);

            NativeMethods.SetForegroundWindow(handle);
            Activate();
        }
        catch
        {
        }
        finally
        {
            _placed = true;
        }
    }

    private static double GetScreenDpi(System.Drawing.Rectangle work)
    {
        try
        {
            var monitor = NativeMethods.MonitorFromPoint(
                new NativeMethods.POINT { X = work.Left + 1, Y = work.Top + 1 },
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
        }

        return 1.0;
    }

    private void AnimateIn()
    {
        if (SettingsService.Current.ReducedMotion)
        {
            Opacity = 1;
            Slide.Y = 0;
            return;
        }

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120))
        {
            EasingFunction = ease
        };
        BeginAnimation(OpacityProperty, fade);

        var slide = new DoubleAnimation(5, 0, TimeSpan.FromMilliseconds(140))
        {
            EasingFunction = ease
        };
        Slide.BeginAnimation(TranslateTransform.YProperty, slide);
    }

    private void Window_Deactivated(object sender, EventArgs e)
    {
        if (!_placed)
        {
            return;
        }

        Dispatcher.BeginInvoke(new Action(Close), DispatcherPriority.Background);
    }

    private void TrayMenuRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TrayMenuItem item } || item.Invoke is null)
        {
            return;
        }

        var invoke = item.Invoke;
        Close();
        invoke();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }

        var buttons = VisibleRows();
        if (buttons.Count == 0)
        {
            return;
        }

        if (e.Key is Key.Down or Key.Up or Key.Home or Key.End)
        {
            var focused = Keyboard.FocusedElement as Button;
            var index = focused is null ? -1 : buttons.IndexOf(focused);

            index = e.Key switch
            {
                Key.Down => index < 0 ? 0 : Math.Min(index + 1, buttons.Count - 1),
                Key.Up => index < 0 ? buttons.Count - 1 : Math.Max(index - 1, 0),
                Key.Home => 0,
                _ => buttons.Count - 1
            };

            Keyboard.Focus(buttons[index]);
            e.Handled = true;
        }
        else if (e.Key == Key.Tab && Keyboard.FocusedElement is not Button)
        {
            var first = e.KeyboardDevice.Modifiers.HasFlag(ModifierKeys.Shift)
                ? buttons[buttons.Count - 1]
                : buttons[0];
            Keyboard.Focus(first);
            e.Handled = true;
        }
    }

    private List<Button> VisibleRows()
    {
        var buttons = new List<Button>();
        for (var index = 0; index < ItemsHost.Items.Count; index++)
        {
            if (ItemsHost.ItemContainerGenerator.ContainerFromIndex(index) is not ContentPresenter presenter)
            {
                continue;
            }

            presenter.ApplyTemplate();
            if (FindChild<Button>(presenter) is { IsVisible: true } button)
            {
                buttons.Add(button);
            }
        }

        return buttons;
    }

    private static T? FindChild<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T typed)
            {
                return typed;
            }

            if (FindChild<T>(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }
}

public sealed class TrayMenuItem
{
    public string Label { get; init; } = string.Empty;
    public string Glyph { get; init; } = string.Empty;
    public string? Shortcut { get; init; }
    public bool? IsOn { get; init; }
    public bool IsDanger { get; init; }
    public bool IsSeparator { get; init; }
    public Action? Invoke { get; init; }

    public string StateLabel => IsOn switch
    {
        true => "On",
        false => "Off",
        _ => string.Empty
    };

    public static TrayMenuItem Separator() => new() { IsSeparator = true };
}
