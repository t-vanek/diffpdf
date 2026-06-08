using System.Globalization;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;

namespace DiffPdf.DesktopUI.Controls;

/// <summary>
/// A <see cref="TextBlock"/> whose numeric <see cref="Value"/> rolls smoothly to its new target — counting up
/// from 0 on first show and easing to the new figure on later updates — via a transition on <see cref="Value"/>.
/// Bind <see cref="Value"/> to an int/double; the displayed text is the rounded current (animated) value.
/// </summary>
public class RollingNumber : TextBlock
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<RollingNumber, double>(nameof(Value));

    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public RollingNumber()
    {
        Text = "0";
        // A transition on Value means any change (incl. binding updates) tweens through intermediate values;
        // OnPropertyChanged then re-renders the rounded figure each frame → a rolling counter.
        Transitions = new Transitions
        {
            new DoubleTransition { Property = ValueProperty, Duration = TimeSpan.FromSeconds(0.55), Easing = new CubicEaseOut() },
        };
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ValueProperty)
            Text = ((long)Math.Round(Value)).ToString(CultureInfo.CurrentCulture);
    }
}
