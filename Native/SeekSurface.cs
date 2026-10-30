using System;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace TolkIsland;

/// <summary>
/// The media progress bar. A Grid so the existing track, fill and knob stay
/// unchanged, but focusable, arrow-key operable and exposed to UI Automation as
/// a RangeValue instead of an unnamed graphic.
/// </summary>
public sealed class SeekSurface : Grid
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value),
        typeof(double),
        typeof(SeekSurface),
        new FrameworkPropertyMetadata(0.0));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum),
        typeof(double),
        typeof(SeekSurface),
        new FrameworkPropertyMetadata(0.0));

    public SeekSurface()
    {
        Focusable = true;
        KeyboardNavigation.SetIsTabStop(this, true);
        KeyDown += OnKeyDown;
    }

    /// <summary>Fired for keyboard and UI Automation seeks; seconds from the start.</summary>
    public event EventHandler<double>? SeekRequested;

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public void RequestSeek(double seconds)
    {
        if (Maximum <= 0)
        {
            return;
        }

        SeekRequested?.Invoke(this, Math.Clamp(seconds, 0, Maximum));
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? 30 : 5;
        double? target = e.Key switch
        {
            Key.Left => Value - step,
            Key.Right => Value + step,
            Key.Home => 0,
            Key.End => Maximum,
            _ => null
        };

        if (target is null || Maximum <= 0)
        {
            return;
        }

        e.Handled = true;
        RequestSeek(target.Value);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new SeekSurfacePeer(this);

    private sealed class SeekSurfacePeer : FrameworkElementAutomationPeer, IRangeValueProvider
    {
        public SeekSurfacePeer(SeekSurface owner)
            : base(owner)
        {
        }

        private SeekSurface Surface => (SeekSurface)Owner;

        public bool IsReadOnly => Surface.Maximum <= 0;

        public double LargeChange => 30;

        public double Maximum => Surface.Maximum;

        public double Minimum => 0;

        public double SmallChange => 5;

        public double Value => Surface.Value;

        public override object? GetPattern(PatternInterface patternInterface)
            => patternInterface == PatternInterface.RangeValue
                ? this
                : base.GetPattern(patternInterface);

        public void SetValue(double value) => Surface.RequestSeek(value);

        protected override AutomationControlType GetAutomationControlTypeCore()
            => AutomationControlType.Slider;

        protected override string GetClassNameCore() => "SeekSurface";
    }
}
