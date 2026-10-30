using System.Windows;
using System.Windows.Input;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using SystemColors = System.Windows.SystemColors;

namespace TolkIsland;

public partial class MessageWindow : Window
{
    private MessageWindow()
    {
        InitializeComponent();
        ApplyHighContrastChrome();
    }

    private void ApplyHighContrastChrome()
    {
        if (!SystemParameters.HighContrast)
        {
            return;
        }

        Resources["SurfaceBrush"] = SystemColors.WindowBrush;
        Resources["ShadowSurfaceBrush"] = SystemColors.WindowBrush;
        Resources["SurfaceBorderBrush"] = SystemColors.WindowTextBrush;
        Resources["TitleInkBrush"] = SystemColors.WindowTextBrush;
        Resources["MessageInkBrush"] = SystemColors.WindowTextBrush;
        Resources["LogoPlateSurfaceBrush"] = SystemColors.ControlBrush;
        Resources["LogoPlateBorderBrush"] = SystemColors.WindowTextBrush;
        Resources["FocusRingBrush"] = SystemColors.WindowTextBrush;
        Resources["SecondaryButtonSurfaceBrush"] = SystemColors.ControlBrush;
        Resources["SecondaryButtonInkBrush"] = SystemColors.WindowTextBrush;
        Resources["SecondaryButtonBorderBrush"] = SystemColors.WindowTextBrush;
        Resources["SecondaryButtonHoverBrush"] = SystemColors.ControlDarkBrush;
        Resources["SecondaryButtonPressedBrush"] = SystemColors.ControlDarkDarkBrush;
        Resources["PrimaryButtonSurfaceBrush"] = SystemColors.HighlightBrush;
        Resources["PrimaryButtonInkBrush"] = SystemColors.HighlightTextBrush;
        Resources["PrimaryButtonHoverBrush"] = SystemColors.HighlightBrush;
        Resources["PrimaryButtonPressedBrush"] = SystemColors.HighlightBrush;
    }

    public static bool Show(string title, string message, string primaryLabel, string? secondaryLabel = null)
    {
        var dialog = new MessageWindow();
        dialog.TitleText.Text = title;
        dialog.MessageText.Text = message;
        dialog.PrimaryButton.Content = primaryLabel;

        if (string.IsNullOrWhiteSpace(secondaryLabel))
        {
            dialog.SecondaryButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            dialog.SecondaryButton.Content = secondaryLabel;
        }

        dialog.Loaded += (_, _) =>
        {
            dialog.Activate();
            dialog.PrimaryButton.Focus();
        };

        return dialog.ShowDialog() == true;
    }

    private void Primary_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Secondary_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
            e.Handled = true;
        }
    }
}
