using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TolkIsland.Services;

namespace TolkIsland.Regression;

internal static class IslandRender
{
    public static int Run(string outputDirectory, string? accent = null, string? skin = null)
    {
        var dataDirectory = Path.Combine(
            Path.GetTempPath(), "tolkisland-render-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDirectory);
        SettingsService.OverrideDataDirectory(dataDirectory);
        DiagnosticsService.RequestLoggingEnabled = false;

        if (!string.IsNullOrWhiteSpace(accent) || !string.IsNullOrWhiteSpace(skin))
        {
            SettingsService.Update(settings =>
            {
                if (!string.IsNullOrWhiteSpace(accent))
                {
                    settings.AccentColor = accent;
                }

                settings.Skin = ParseSkin(skin);
            });
        }

        try
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Resources["ArtPlaceholder"] = new LinearGradientBrush(
                (Color)ColorConverter.ConvertFromString("#FF9B6BFF"),
                (Color)ColorConverter.ConvertFromString("#FFFFA45B"),
                new Point(0, 0),
                new Point(1, 1));
            app.Resources["TolkLogoMark"] = BuildLogoMark();

            Directory.CreateDirectory(outputDirectory);

            var window = new MainWindow(isPrimary: false);
            var content = (FrameworkElement)window.Content;

            VerifyMotionParts(window);

            ShowOnly(window, window.IdlePanel);
            SetShell(window, 300, 46);
            FillClock(window);
            Save(content, outputDirectory, "preview-idle-pill.png",
                new Int32Rect(160, 0, 300, 46));

            ShowOnly(window, window.IdleExpandedPanel);
            SetShell(window, 500, 226);
            FillHome(window);
            Save(content, outputDirectory, "preview-idle-home.png",
                new Int32Rect(60, 0, 500, 226));

            ShowOnly(window, window.IdleHiddenPanel);
            SetShell(window, 160, 10);
            Save(content, outputDirectory, "preview-idle-hidden.png",
                new Int32Rect(230, 0, 160, 10));

            ShowOnly(window, window.ExpandedPanel);
            SetShell(window, 460, 172);
            FillPlayer(window);
            Save(content, outputDirectory, "preview-player.png",
                new Int32Rect(80, 0, 460, 172));

            SettingsService.Update(settings => settings.Theme = ThemeMode.Light);

            var light = new MainWindow(isPrimary: false);
            var lightContent = (FrameworkElement)light.Content;

            ShowOnly(light, light.IdleExpandedPanel);
            SetShell(light, 500, 226);
            FillHome(light);
            Save(lightContent, outputDirectory, "preview-idle-home-light.png",
                new Int32Rect(60, 0, 500, 226));

            ShowOnly(light, light.ExpandedPanel);
            SetShell(light, 460, 172);
            FillPlayer(light);
            Save(lightContent, outputDirectory, "preview-player-light.png",
                new Int32Rect(80, 0, 460, 172));

            Console.WriteLine($"Rendered island previews to {outputDirectory}");
            return 0;
        }
        finally
        {
            SettingsService.OverrideDataDirectory(null);
            try
            {
                Directory.Delete(dataDirectory, recursive: true);
            }
            catch
            {
            }
        }
    }

    public static int RunPromo(string outputDirectory, string? accent = null)
    {
        var dataDirectory = Path.Combine(
            Path.GetTempPath(), "tolkisland-promo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDirectory);
        SettingsService.OverrideDataDirectory(dataDirectory);
        DiagnosticsService.RequestLoggingEnabled = false;
        SettingsService.Update(settings =>
        {
            settings.AccentColor = string.IsNullOrWhiteSpace(accent) ? "#FFFF6B6B" : accent;
            settings.Theme = ThemeMode.Dark;
        });

        try
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Resources["ArtPlaceholder"] = new LinearGradientBrush(
                (Color)ColorConverter.ConvertFromString("#FF9B6BFF"),
                (Color)ColorConverter.ConvertFromString("#FFFFA45B"),
                new Point(0, 0),
                new Point(1, 1));
            app.Resources["TolkLogoMark"] = BuildLogoMark();

            Directory.CreateDirectory(outputDirectory);

            var window = new MainWindow(isPrimary: false);
            var content = (FrameworkElement)window.Content;

            ShowOnly(window, window.IdleExpandedPanel);
            SetShell(window, 500, 226);
            FillHome(window);
            Save(content, outputDirectory, "idle-home-dark@2x.png",
                new Int32Rect(60, 0, 500, 226), 2);

            ShowOnly(window, window.ExpandedPanel);
            SetShell(window, 460, 172);
            FillPlayer(window);
            Save(content, outputDirectory, "player-dark@2x.png",
                new Int32Rect(80, 0, 460, 172), 2);

            ShowOnly(window, window.NotificationPanel);
            SetShell(window, 460, 118);
            FillNotification(window);
            Save(content, outputDirectory, "notification-dark@2x.png",
                new Int32Rect(80, 0, 460, 118), 2);

            ShowOnly(window, window.FocusCompletePanel);
            SetShell(window, 460, 88);
            FillFocusComplete(window);
            Save(content, outputDirectory, "focus-complete-dark@2x.png",
                new Int32Rect(80, 0, 460, 88), 2);

            ShowOnly(window, window.FocusPickerPanel);
            SetShell(window, 460, 168);
            Save(content, outputDirectory, "focus-picker-dark@2x.png",
                new Int32Rect(80, 0, 460, 168), 2);

            ShowOnly(window, window.CompactPanel);
            SetShell(window, 300, 50);
            FillCompact(window);
            Save(content, outputDirectory, "compact-media-dark@2x.png",
                new Int32Rect(160, 0, 300, 50), 2);

            SettingsService.Update(settings => settings.Theme = ThemeMode.Light);

            var light = new MainWindow(isPrimary: false);
            var lightContent = (FrameworkElement)light.Content;

            ShowOnly(light, light.IdleExpandedPanel);
            SetShell(light, 500, 226);
            FillHome(light);
            Save(lightContent, outputDirectory, "idle-home-light@2x.png",
                new Int32Rect(60, 0, 500, 226), 2);

            ShowOnly(light, light.ExpandedPanel);
            SetShell(light, 460, 172);
            FillPlayer(light);
            Save(lightContent, outputDirectory, "player-light@2x.png",
                new Int32Rect(80, 0, 460, 172), 2);

            ShowOnly(light, light.NotificationPanel);
            SetShell(light, 460, 118);
            FillNotification(light);
            Save(lightContent, outputDirectory, "notification-light@2x.png",
                new Int32Rect(80, 0, 460, 118), 2);

            ShowOnly(light, light.FocusCompletePanel);
            SetShell(light, 460, 88);
            FillFocusComplete(light);
            Save(lightContent, outputDirectory, "focus-complete-light@2x.png",
                new Int32Rect(80, 0, 460, 88), 2);

            ShowOnly(light, light.FocusPickerPanel);
            SetShell(light, 460, 168);
            Save(lightContent, outputDirectory, "focus-picker-light@2x.png",
                new Int32Rect(80, 0, 460, 168), 2);

            ShowOnly(light, light.CompactPanel);
            SetShell(light, 300, 50);
            FillCompact(light);
            Save(lightContent, outputDirectory, "compact-media-light@2x.png",
                new Int32Rect(160, 0, 300, 50), 2);

            Console.WriteLine($"Rendered promo stills to {outputDirectory}");
            return 0;
        }
        finally
        {
            SettingsService.OverrideDataDirectory(null);
            try
            {
                Directory.Delete(dataDirectory, recursive: true);
            }
            catch
            {
            }
        }
    }

    public static int RunScreens(string outputDirectory, string? accent = null, string? skin = null)
    {
        var dataDirectory = Path.Combine(
            Path.GetTempPath(), "tolkisland-screens-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDirectory);
        SettingsService.OverrideDataDirectory(dataDirectory);
        DiagnosticsService.RequestLoggingEnabled = false;

        try
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Resources["ArtPlaceholder"] = new LinearGradientBrush(
                (Color)ColorConverter.ConvertFromString("#FF9B6BFF"),
                (Color)ColorConverter.ConvertFromString("#FFFFA45B"),
                new Point(0, 0),
                new Point(1, 1));
            app.Resources["TolkLogoMark"] = BuildLogoMark();

            Directory.CreateDirectory(outputDirectory);

            SettingsService.Update(settings =>
            {
                settings.AccentColor = string.IsNullOrWhiteSpace(accent) ? "#FFFF6B6B" : accent;
                settings.Skin = ParseSkin(skin);
                settings.Theme = ThemeMode.Dark;
            });
            RenderScreenSet(outputDirectory, "dark");

            SettingsService.Update(settings => settings.Theme = ThemeMode.Light);
            RenderScreenSet(outputDirectory, "light");

            RenderTray(outputDirectory);
            RenderDialog(outputDirectory);
            RenderSettings(outputDirectory);

            Console.WriteLine($"Rendered screen stills to {outputDirectory}");
            return 0;
        }
        finally
        {
            SettingsService.OverrideDataDirectory(null);
            try
            {
                Directory.Delete(dataDirectory, recursive: true);
            }
            catch
            {
            }
        }
    }

    private static SkinMode ParseSkin(string? value)
        => string.Equals(value, "spider", StringComparison.OrdinalIgnoreCase)
            ? SkinMode.Spider
            : SkinMode.Classic;

    private static void RenderScreenSet(string outputDirectory, string theme)
    {
        var folder = Path.Combine(outputDirectory, theme);
        Directory.CreateDirectory(folder);

        var window = new MainWindow(isPrimary: false);
        var content = (FrameworkElement)window.Content;

        Shot(window, content, folder, "01-idle-pill", window.IdlePanel, 300, 46, FillClock, buddy: true);
        Shot(window, content, folder, "02-idle-home", window.IdleExpandedPanel, 500, 172, FillHome, buddy: true);
        Shot(window, content, folder, "02b-idle-home-mirror", window.IdleExpandedPanel, 500, 172, FillHomeWithMirror, buddy: true);
        Shot(window, content, folder, "03-player", window.ExpandedPanel, 460, 172, FillPlayer, buddy: true);
        Shot(window, content, folder, "04-compact-media", window.CompactPanel, 300, 50, FillCompact, buddy: true);
        Shot(window, content, folder, "05-compact-focus", window.CompactFocusPanel, 300, 50, FillCompactFocus);
        Shot(window, content, folder, "06-focus-picker", window.FocusPickerPanel, 460, 168);
        Shot(window, content, folder, "07-timer", window.TimerPanel, 460, 160, FillTimer);
        Shot(window, content, folder, "08-focus-complete", window.FocusCompletePanel, 460, 88, FillFocusComplete);
        Shot(window, content, folder, "09-notification", window.NotificationPanel, 460, 118, FillNotification);
        Shot(window, content, folder, "10-charging", window.ChargingPanel, 380, 72, FillCharging);
        Shot(window, content, folder, "11-volume", window.VolumePanel, 440, 72, FillVolume);
        Shot(window, content, folder, "12-brightness", window.BrightnessPanel, 440, 72, FillBrightness);
        Shot(window, content, folder, "13-shelf", window.ShelfPanel, 460, 190, FillShelf);
        Shot(window, content, folder, "14-notes", window.NotesPanel, 460, 208, FillNotes);
        Shot(window, content, folder, "15-calendar", window.CalendarPanel, 460, 150, FillCalendar);
        Shot(window, content, folder, "16-ocr", window.OcrPanel, 460, 260, FillOcr);
        Shot(window, content, folder, "17-live-activity", window.LiveActivityPanel, 460, 88, FillLiveActivity);
        Shot(window, content, folder, "18-mirror", window.MirrorPanel, 480, 280, FillMirror);
        Shot(window, content, folder, "18b-mirror-starting", window.MirrorPanel, 480, 280, FillMirrorStarting);
        Shot(window, content, folder, "18c-mirror-denied", window.MirrorPanel, 480, 280, FillMirrorDenied);

        Shot(window, content, folder, "19-shelf-empty", window.ShelfPanel, 460, 190, EmptyShelf);
        Shot(window, content, folder, "20-notes-empty", window.NotesPanel, 460, 208, EmptyNotes);
        Shot(window, content, folder, "21-calendar-empty", window.CalendarPanel, 460, 150, EmptyCalendar);
    }

    private static void Shot(
        MainWindow window,
        FrameworkElement content,
        string folder,
        string name,
        UIElement panel,
        double width,
        double height,
        Action<MainWindow>? fill = null,
        bool buddy = false)
    {
        ShowOnly(window, panel);
        SetShell(window, width, height);
        fill?.Invoke(window);

        window.ShowSpiderBuddy(buddy);
        var extra = buddy && window.SpiderBuddy.Visibility == Visibility.Visible
            ? (int)Math.Ceiling(SpiderSkin.BuddyHeight) + 6
            : 0;

        Save(content, folder, name + ".png",
            new Int32Rect((int)Math.Round((620 - width) / 2), 0, (int)Math.Round(width), (int)Math.Round(height) + extra), 2);
    }

    private static void FillTimer(MainWindow window)
    {
        window.TimerRemainingText.Text = "18:24";
        window.TimerSubtitleText.Text = "25 minute focus · stay with it";
        DrawArc(window, "DrawTimerArc", 0.62);
    }

    private static void FillCompactFocus(MainWindow window)
    {
        window.CompactFocusTime.Text = "18:24";
        window.CompactFocusLabel.Text = "Focus";
        DrawArc(window, "DrawCompactTimerArc", 0.62);
    }

    private static void FillCharging(MainWindow window)
    {
        window.ChargingBatteryFill.Width = 20;
        window.ChargingPercentText.Text = "84%";
    }

    private static void FillVolume(MainWindow window)
    {
        window.VolumeText.Text = "62";
        window.VolumeFill.Width = 155;
    }

    private static void FillBrightness(MainWindow window)
    {
        window.BrightnessText.Text = "45";
        window.BrightnessFill.Width = 112;
    }

    private static void FillShelf(MainWindow window)
    {
        window.ShelfItemsPanel.Children.Clear();
        window.ShelfEmptyState.Visibility = Visibility.Collapsed;
        window.ShelfCountText.Text = "3 files";

        AddShelfRow(window, "Design system.fig", "12.4 MB");
        AddShelfRow(window, "Moodboard.png", "4.8 MB");
        AddShelfRow(window, "Launch brief.pdf", "820 KB");
    }

    private static void EmptyShelf(MainWindow window)
    {
        window.ShelfItemsPanel.Children.Clear();
        window.ShelfEmptyState.Visibility = Visibility.Visible;
        window.ShelfCountText.Text = "empty";
    }

    private static void AddShelfRow(MainWindow window, string name, string size)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var glyph = new TextBlock
        {
            Text = "\uE8A5",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 13,
            Foreground = (Brush)window.FindResource("SecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(3, 0, 9, 0)
        };

        var label = new TextBlock
        {
            Text = name,
            FontFamily = (FontFamily)window.FindResource("UiText"),
            FontSize = 12,
            Foreground = (Brush)window.FindResource("PrimaryBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(label, 1);

        var sizeText = new TextBlock
        {
            Text = size,
            FontFamily = (FontFamily)window.FindResource("UiText"),
            FontSize = 10.5,
            Foreground = (Brush)window.FindResource("SecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0)
        };
        Grid.SetColumn(sizeText, 2);

        var remove = new Button
        {
            Content = "\uE711",
            Style = (Style)window.FindResource("IconButton"),
            Width = 24,
            Height = 24,
            FontSize = 10,
            Margin = new Thickness(4, 0, 0, 0)
        };
        Grid.SetColumn(remove, 3);

        grid.Children.Add(glyph);
        grid.Children.Add(label);
        grid.Children.Add(sizeText);
        grid.Children.Add(remove);

        window.ShelfItemsPanel.Children.Add(new Border
        {
            Style = (Style)window.FindResource("ListCard"),
            Padding = new Thickness(9, 2, 4, 2),
            Child = grid
        });
    }

    private static void FillNotes(MainWindow window)
    {
        window.NotesItemsPanel.Children.Clear();
        window.NotesEmptyState.Visibility = Visibility.Collapsed;
        window.NotesCountText.Text = "3 / 40";

        AddNoteRow(window, "Polish the depth pass", done: false);
        AddNoteRow(window, "Ship the 1.0.0 notes", done: true);
        AddNoteRow(window, "Call the design studio", done: false);
    }

    private static void EmptyNotes(MainWindow window)
    {
        window.NotesItemsPanel.Children.Clear();
        window.NotesEmptyState.Visibility = Visibility.Visible;
        window.NotesCountText.Text = "empty";
    }

    private static void AddNoteRow(MainWindow window, string text, bool done)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var check = new CheckBox
        {
            Style = (Style)window.FindResource("NoteCheckBox"),
            IsChecked = done,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(1, 0, 9, 0)
        };

        var label = new TextBlock
        {
            Text = text,
            FontFamily = (FontFamily)window.FindResource("UiText"),
            FontSize = 12,
            Foreground = (Brush)window.FindResource(done ? "SecondaryBrush" : "PrimaryBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            TextDecorations = done ? TextDecorations.Strikethrough : null
        };
        Grid.SetColumn(label, 1);

        var remove = new Button
        {
            Content = "\uE711",
            Style = (Style)window.FindResource("IconButton"),
            Width = 24,
            Height = 24,
            FontSize = 10
        };
        Grid.SetColumn(remove, 2);

        grid.Children.Add(check);
        grid.Children.Add(label);
        grid.Children.Add(remove);

        window.NotesItemsPanel.Children.Add(new Border
        {
            Style = (Style)window.FindResource("ListCard"),
            Padding = new Thickness(11, 2, 6, 2),
            Child = grid
        });
    }

    private static void FillCalendar(MainWindow window)
    {
        window.CalendarItemsPanel.Children.Clear();
        window.CalendarEmptyState.Visibility = Visibility.Collapsed;
        window.CalendarHintText.Visibility = Visibility.Visible;
        window.CalendarCountText.Text = "next";

        AddCalendarRow(window, "10:30", "Design review", joinable: true);
    }

    private static void EmptyCalendar(MainWindow window)
    {
        window.CalendarItemsPanel.Children.Clear();
        window.CalendarEmptyState.Visibility = Visibility.Visible;
        window.CalendarEmptyTitle.Text = "Nothing scheduled";
        window.CalendarEmptyHint.Text = "No appointments in the next 18 hours.";
        window.CalendarCountText.Text = "none";
        window.CalendarHintText.Visibility = Visibility.Collapsed;
    }

    private static void AddCalendarRow(MainWindow window, string when, string title, bool joinable)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var whenText = new TextBlock
        {
            Text = when,
            FontFamily = (FontFamily)window.FindResource("UiText"),
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)window.FindResource("SecondaryBrush")
        };
        System.Windows.Documents.Typography.SetNumeralAlignment(whenText, FontNumeralAlignment.Tabular);

        var pill = new Border
        {
            Background = (Brush)window.FindResource("ChipBrush"),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = whenText
        };

        var label = new TextBlock
        {
            Text = title,
            FontFamily = (FontFamily)window.FindResource("UiText"),
            FontSize = 12,
            Foreground = (Brush)window.FindResource("PrimaryBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(label, 1);

        grid.Children.Add(pill);
        grid.Children.Add(label);

        if (joinable)
        {
            var join = new Button
            {
                Content = "Join",
                Style = (Style)window.FindResource("AccentChipButton"),
                Height = 26,
                FontSize = 11,
                MinWidth = 48,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(join, 2);
            grid.Children.Add(join);
        }

        window.CalendarItemsPanel.Children.Add(new Border
        {
            Style = (Style)window.FindResource("ListCard"),
            Padding = new Thickness(8, 2, 6, 2),
            Child = grid
        });
    }

    private static void FillMirror(MainWindow window)
    {
        var group = new DrawingGroup();
        var background = new LinearGradientBrush(
            Color.FromRgb(0x2A, 0x33, 0x40),
            Color.FromRgb(0x0A, 0x0D, 0x12),
            new Point(0.2, 0),
            new Point(0.8, 1));
        group.Children.Add(new GeometryDrawing(
            background, null, new RectangleGeometry(new Rect(0, 0, 960, 540))));
        group.Children.Add(new GeometryDrawing(
            new SolidColorBrush(Color.FromRgb(0x1E, 0x27, 0x34)),
            null,
            new EllipseGeometry(new Point(480, 650), 360, 260)));
        group.Children.Add(new GeometryDrawing(
            new SolidColorBrush(Color.FromRgb(0xD9, 0xB6, 0xA2)),
            null,
            new EllipseGeometry(new Point(480, 285), 125, 155)));
        group.Children.Add(new GeometryDrawing(
            new SolidColorBrush(Color.FromRgb(0x2B, 0x22, 0x26)),
            null,
            new EllipseGeometry(new Point(480, 180), 138, 100)));

        var image = new DrawingImage(group);
        image.Freeze();

        window.MirrorPreview.Source = image;
        window.MirrorPreview.Visibility = Visibility.Visible;
        window.MirrorPreviewScale.ScaleX = -1;
        window.MirrorPreviewScale.ScaleY = 1;
        window.MirrorShutter.Visibility = Visibility.Collapsed;
        window.MirrorLiveChip.Visibility = Visibility.Visible;
        window.MirrorActions.Visibility = Visibility.Collapsed;
    }

    private static void FillMirrorStarting(MainWindow window)
    {
        window.MirrorPreview.Visibility = Visibility.Collapsed;
        window.MirrorShutter.Visibility = Visibility.Visible;
        window.MirrorShutter.Opacity = 1;
        window.MirrorLiveChip.Visibility = Visibility.Collapsed;
        window.MirrorStatusTitle.Text = "Starting camera…";
        window.MirrorStatusHint.Text = "Preview only. Nothing is recorded.";
        window.MirrorActions.Visibility = Visibility.Collapsed;
    }

    private static void FillMirrorDenied(MainWindow window)
    {
        FillMirrorStarting(window);
        window.MirrorStatusGlyph.Text = "";
        window.MirrorStatusTitle.Text = "Camera access is off";
        window.MirrorStatusHint.Text = "Allow camera access in Windows Settings, then try again.";
        window.MirrorActions.Visibility = Visibility.Visible;
        window.MirrorRetryButton.Visibility = Visibility.Visible;
        window.MirrorSettingsButton.Visibility = Visibility.Visible;
    }

    private static void FillOcr(MainWindow window)
    {
        window.OcrText.Text =
            "Design review\n" +
            "Wednesday, 30 October\n" +
            "10:30 - 11:15\n\n" +
            "Open the prototype and review the new mirror states.\n" +
            "Bring the latest screenshots and a short list of open questions.";
        window.OcrScroller.Visibility = Visibility.Visible;
        window.OcrEmptyState.Visibility = Visibility.Collapsed;
        window.OcrCopyButton.Visibility = Visibility.Visible;
        window.OcrLanguageButton.Visibility = Visibility.Collapsed;
        window.OcrCountText.Text = "6 lines";
        window.OcrStatusText.Text = "English (United States) · 14:32";
    }

    private static void FillLiveActivity(MainWindow window)
    {
        window.LiveActivityGlyph.Text = "\uE896";
        window.LiveActivityTitle.Text = "Downloading";
        window.LiveActivityBody.Text = "Moodboard.png · 4.8 MB of 12.4 MB · 2.1 MB/s";
        window.LiveActivityTrack.Visibility = Visibility.Visible;
        window.LiveActivityFill.Width = 120;
    }

    private static void DrawArc(MainWindow window, string method, double progress)
    {
        var target = typeof(MainWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic);
        if (target is null)
        {
            throw new InvalidOperationException($"MainWindow.{method} is missing.");
        }

        target.Invoke(window, new object[] { progress });
    }

    private static void RenderTray(string outputDirectory)
    {
        var menu = new TrayMenuWindow(new List<TrayMenuItem>
        {
            new() { Label = "Show / hide island", Glyph = "", Shortcut = "Ctrl+Alt+T" },
            new() { Label = "Pause island", Glyph = "", IsOn = false },
            new() { Label = "Settings…", Glyph = "" },
            new() { Label = "Exit", Glyph = "", IsDanger = true }
        });

        var root = (FrameworkElement)menu.Content;
        root.Measure(new Size(340, double.PositiveInfinity));
        var height = Math.Ceiling(root.DesiredSize.Height);
        root.Arrange(new Rect(0, 0, 340, height));
        root.UpdateLayout();

        SaveBitmap(root, Path.Combine(outputDirectory, "tray-menu@2x.png"), 340, height, 2);
    }

    private static void RenderDialog(string outputDirectory)
    {
        var dialog = (MessageWindow)Activator.CreateInstance(typeof(MessageWindow), nonPublic: true)!;
        dialog.TitleText.Text = "Update available";
        dialog.MessageText.Text =
            "Tolk Island 1.1.0 is ready. Your settings, notes and shortcuts are unchanged. " +
            "The update only replaces the app.";
        dialog.PrimaryButton.Content = "Get the update";
        dialog.SecondaryButton.Content = "Not now";

        var root = (FrameworkElement)dialog.Content;
        root.Measure(new Size(440, double.PositiveInfinity));
        var height = Math.Ceiling(root.DesiredSize.Height);
        root.Arrange(new Rect(0, 0, 440, height));
        root.UpdateLayout();

        SaveBitmap(root, Path.Combine(outputDirectory, "dialog@2x.png"), 440, height, 2);
    }

    private static DrawingImage BuildLogoMark()
    {
        var bars = new GeometryGroup();
        bars.Children.Add(new RectangleGeometry(new Rect(73.5, 54, 21, 45), 10.5, 10.5));
        bars.Children.Add(new RectangleGeometry(new Rect(114.5, 32.5, 21, 88), 10.5, 10.5));
        bars.Children.Add(new RectangleGeometry(new Rect(155.5, 44.5, 21, 64), 10.5, 10.5));

        var drawing = new DrawingGroup();
        drawing.Children.Add(new GeometryDrawing(
            new SolidColorBrush(Color.FromRgb(0x00, 0x00, 0x00)),
            null,
            new RectangleGeometry(new Rect(0, 0, 250, 153), 76.5, 76.5)));
        drawing.Children.Add(new GeometryDrawing(
            new SolidColorBrush(Color.FromRgb(0xFF, 0xB3, 0x40)),
            null,
            bars));

        var image = new DrawingImage(drawing);
        image.Freeze();
        return image;
    }

    private static void RenderSettings(string outputDirectory)
    {
        var folder = Path.Combine(outputDirectory, "settings");
        Directory.CreateDirectory(folder);

        var window = new SettingsWindow();
        var root = (FrameworkElement)window.Content;
        root.Measure(new Size(960, 740));
        root.Arrange(new Rect(0, 0, 960, 740));
        root.UpdateLayout();

        SaveBitmap(root, Path.Combine(folder, "settings-window.png"), 960, 740, 1);

        window.SettingsTabs.SelectedIndex = 3;
        root.UpdateLayout();
        SaveBitmap(root, Path.Combine(folder, "settings-activities.png"), 960, 740, 1);

        window.SettingsTabs.SelectedIndex = 4;
        root.UpdateLayout();
        SaveBitmap(root, Path.Combine(folder, "settings-advanced.png"), 960, 740, 1);

        window.SettingsTabs.SelectedIndex = 0;
        root.UpdateLayout();

        window.PrepareOffscreenLayout(660, 340);
        root.Measure(new Size(660, 340));
        root.Arrange(new Rect(0, 0, 660, 340));
        root.UpdateLayout();

        SaveBitmap(root, Path.Combine(folder, "settings-window-compact.png"), 660, 340, 1);
    }

    private static void SaveBitmap(FrameworkElement element, string path, double width, double height, double scale)
    {
        var bitmap = new RenderTargetBitmap(
            (int)Math.Round(width * scale),
            (int)Math.Round(height * scale),
            96 * scale,
            96 * scale,
            PixelFormats.Pbgra32);
        bitmap.Render(element);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void VerifyMotionParts(MainWindow window)
    {
        foreach (var button in new[]
                 {
                     window.FocusEntryButton, window.ShelfEntryButton, window.NotesEntryButton,
                     window.CalendarEntryButton, window.OcrEntryButton, window.MirrorEntryButton,
                     window.MirrorCloseButton, window.PlayPauseButton, window.ShuffleButton
                 })
        {
            button.ApplyTemplate();
            if (button.Template?.FindName("HoverLayer", button) is not Border
                || button.Template?.FindName("PressLayer", button) is not Border
                || button.Template?.FindName("PressScale", button) is not ScaleTransform)
            {
                throw new InvalidOperationException(
                    $"Button motion template parts are missing on {button.Name}.");
            }
        }
    }

    private static void ShowOnly(MainWindow window, UIElement panel)
    {
        foreach (var candidate in new UIElement[]
                 {
                     window.IdlePanel, window.IdleHiddenPanel, window.IdleExpandedPanel,
                     window.ExpandedPanel, window.CompactPanel, window.CompactFocusPanel,
                     window.NotificationPanel, window.FocusCompletePanel, window.FocusPickerPanel,
                     window.TimerPanel, window.ChargingPanel, window.VolumePanel, window.BrightnessPanel,
                     window.NotesPanel, window.ShelfPanel, window.CalendarPanel,
                     window.OcrPanel, window.MirrorPanel, window.LiveActivityPanel
                 })
        {
            candidate.Visibility = Visibility.Collapsed;
        }

        panel.Visibility = Visibility.Visible;
    }

    private static void SetShell(MainWindow window, double width, double height)
    {
        window.Shell.Width = width;
        window.Shell.Height = height;
    }

    private static void FillClock(MainWindow window)
    {
        window.ClockText.Text = "21:42";
        window.BatteryText.Text = "84%";
        window.BatteryFill.Width = 10;
        window.QuietGlyph.Visibility = Visibility.Collapsed;
    }

    private static void FillHome(MainWindow window)
    {
        FillClock(window);
        window.BigClockText.Text = "21:42";
        window.DateText.Text = "Wednesday, 28 October";
        window.HomeBatteryText.Text = "84%";
        window.HomeBatteryFill.Width = 10;

        window.QuickLaunchPanel.Children.Clear();
    }

    private static void FillHomeWithMirror(MainWindow window)
    {
        FillHome(window);

        if (!window.IdleHomeDock.Children.Contains(window.MirrorEntryButton))
        {
            window.IdleHomeDock.Children.Add(window.MirrorEntryButton);
        }

        window.IdleHomeDock.Columns = 5;
    }

    private static void FillPlayer(MainWindow window)
    {
        window.ExpandedTitle.Text = "Night Drive";
        window.ExpandedArtist.Text = "Aurora";
        window.PositionText.Text = "1:24";
        window.DurationText.Text = "3:58";
        window.ProgressTrack.UpdateLayout();
        window.ProgressFill.Width = 120;
        window.ProgressKnobTransform.X = 116;
        window.ProgressKnob.Visibility = Visibility.Visible;
        window.PlayPauseGlyph.Text = "\uE769";
    }

    private static void FillNotification(MainWindow window)
    {
        window.AvatarText.Text = "M";
        window.NotifHeaderText.Text = "Messages · now";
        window.NotifBodyText.Text = "Maya: On my way";
    }

    private static void FillFocusComplete(MainWindow window)
    {
        window.FocusCompleteSubtitle.Text = "25 minutes · nicely done";
    }

    private static void FillCompact(MainWindow window)
    {
        double[] levels = { 0.35, 0.62, 0.95, 0.5, 0.78 };
        var brush = (Brush)window.FindResource("WaveBrush");
        window.WaveformHost.Children.Clear();

        foreach (var level in levels)
        {
            window.WaveformHost.Children.Add(new Border
            {
                Width = 3.5,
                Height = 24,
                CornerRadius = new CornerRadius(1.75),
                Margin = new Thickness(2.5, 0, 2.5, 0),
                Background = brush,
                RenderTransform = new ScaleTransform(1, level),
                RenderTransformOrigin = new Point(0.5, 0.5)
            });
        }
    }

    private static void Save(FrameworkElement content, string folder, string name, Int32Rect crop, double scale = 1)
    {
        content.Measure(new Size(620, 300));
        content.Arrange(new Rect(0, 0, 620, 300));
        content.UpdateLayout();

        var bitmap = new RenderTargetBitmap(
            (int)Math.Round(620 * scale),
            (int)Math.Round(300 * scale),
            96 * scale,
            96 * scale,
            PixelFormats.Pbgra32);
        bitmap.Render(content);

        var scaledCrop = new Int32Rect(
            (int)Math.Round(crop.X * scale),
            (int)Math.Round(crop.Y * scale),
            (int)Math.Round(crop.Width * scale),
            (int)Math.Round(crop.Height * scale));

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(new CroppedBitmap(bitmap, scaledCrop)));
        using var stream = File.Create(Path.Combine(folder, name));
        encoder.Save(stream);
    }
}
