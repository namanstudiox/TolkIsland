using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Button = System.Windows.Controls.Button;

namespace TolkIsland;

internal static class ButtonMotion
{
    private static readonly TimeSpan HoverIn = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan HoverOut = TimeSpan.FromMilliseconds(180);
    private static readonly TimeSpan PressIn = TimeSpan.FromMilliseconds(80);
    private static readonly TimeSpan PressOut = TimeSpan.FromMilliseconds(240);

    public static bool ReducedMotion { get; set; }

    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(ButtonMotion), new PropertyMetadata(false, OnEnabledChanged));

    public static readonly DependencyProperty PressScaleProperty = DependencyProperty.RegisterAttached(
        "PressScale", typeof(double), typeof(ButtonMotion), new PropertyMetadata(0.95));

    public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);

    public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);

    public static void SetPressScale(DependencyObject element, double value)
        => element.SetValue(PressScaleProperty, value);

    public static double GetPressScale(DependencyObject element) => (double)element.GetValue(PressScaleProperty);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Button button)
        {
            return;
        }

        if (e.NewValue is true)
        {
            button.MouseEnter += OnEnter;
            button.MouseLeave += OnLeave;
            button.PreviewMouseLeftButtonDown += OnDown;
            button.PreviewMouseLeftButtonUp += OnUp;
            button.Unloaded += OnUnloaded;
        }
        else
        {
            Detach(button);
            Reset(button);
        }
    }

    private static void Detach(Button button)
    {
        button.MouseEnter -= OnEnter;
        button.MouseLeave -= OnLeave;
        button.PreviewMouseLeftButtonDown -= OnDown;
        button.PreviewMouseLeftButtonUp -= OnUp;
        button.Unloaded -= OnUnloaded;
    }

    private static void OnEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is Button button)
        {
            Fade(button, "HoverLayer", 1, HoverIn, null);
        }
    }

    private static void OnLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is not Button button)
        {
            return;
        }

        Fade(button, "HoverLayer", 0, HoverOut, null);
        Fade(button, "PressLayer", 0, HoverOut, null);
        Scale(button, 1, PressOut, BackEase());
    }

    private static void OnDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not Button button)
        {
            return;
        }

        Fade(button, "PressLayer", 1, PressIn, null);
        Scale(button, GetPressScale(button), PressIn, null);
    }

    private static void OnUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not Button button)
        {
            return;
        }

        Fade(button, "PressLayer", 0, HoverOut, null);
        Scale(button, 1, PressOut, BackEase());
    }

    private static void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is Button button)
        {
            Reset(button);
        }
    }

    private static void Reset(Button button)
    {
        ResetPart(button, "HoverLayer");
        ResetPart(button, "PressLayer");

        if (button.Template?.FindName("PressScale", button) is ScaleTransform scale)
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            scale.ScaleX = 1;
            scale.ScaleY = 1;
        }
    }

    private static void ResetPart(Button button, string name)
    {
        if (button.Template?.FindName(name, button) is Border border)
        {
            border.BeginAnimation(UIElement.OpacityProperty, null);
            border.Opacity = 0;
        }
    }

    private static void Fade(
        Button button,
        string part,
        double to,
        TimeSpan duration,
        IEasingFunction? easing)
    {
        if (button.Template?.FindName(part, button) is not Border border)
        {
            return;
        }

        if (ReducedMotion)
        {
            border.BeginAnimation(UIElement.OpacityProperty, null);
            border.Opacity = to;
            return;
        }

        var from = border.Opacity;
        if (Math.Abs(from - to) < 0.001 && !border.HasAnimatedProperties)
        {
            return;
        }

        var animation = new DoubleAnimation(from, to, new Duration(duration))
        {
            EasingFunction = easing,
            FillBehavior = FillBehavior.Stop
        };

        animation.Completed += (_, _) =>
        {
            border.BeginAnimation(UIElement.OpacityProperty, null);
            border.Opacity = to;
        };

        border.BeginAnimation(UIElement.OpacityProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private static void Scale(
        Button button,
        double to,
        TimeSpan duration,
        IEasingFunction? easing)
    {
        if (button.Template?.FindName("PressScale", button) is not ScaleTransform scale)
        {
            return;
        }

        if (ReducedMotion)
        {
            ReleaseScale(scale, 1);
            return;
        }

        var animationX = new DoubleAnimation(scale.ScaleX, to, new Duration(duration))
        {
            EasingFunction = easing,
            FillBehavior = FillBehavior.Stop
        };
        var animationY = new DoubleAnimation(scale.ScaleY, to, new Duration(duration))
        {
            EasingFunction = easing,
            FillBehavior = FillBehavior.Stop
        };

        animationX.Completed += (_, _) => ReleaseScale(scale, to);

        scale.BeginAnimation(ScaleTransform.ScaleXProperty, animationX, HandoffBehavior.SnapshotAndReplace);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, animationY, HandoffBehavior.SnapshotAndReplace);
    }

    private static void ReleaseScale(ScaleTransform scale, double value)
    {
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        scale.ScaleX = value;
        scale.ScaleY = value;
    }

    private static IEasingFunction BackEase()
        => new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.20 };
}
