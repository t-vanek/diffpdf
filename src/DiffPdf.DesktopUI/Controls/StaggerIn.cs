using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Styling;

namespace DiffPdf.DesktopUI.Controls;

/// <summary>
/// Attached behaviour for a <see cref="Panel"/>: when attached to the visual tree, its direct children fade in
/// one-by-one (a subtle staggered "content arriving" entrance). Enable with
/// <c>controls:StaggerIn.Enabled="True"</c>. Use on small, non-virtualised panels (a row of cards) — not on
/// long virtualised lists.
/// </summary>
public sealed class StaggerIn
{
    private StaggerIn() { }

    public static readonly AttachedProperty<bool> EnabledProperty =
        AvaloniaProperty.RegisterAttached<StaggerIn, Panel, bool>("Enabled");

    public static void SetEnabled(Panel panel, bool value) => panel.SetValue(EnabledProperty, value);
    public static bool GetEnabled(Panel panel) => panel.GetValue(EnabledProperty);

    static StaggerIn()
    {
        EnabledProperty.Changed.AddClassHandler<Panel>((panel, e) =>
        {
            if (e.GetNewValue<bool>())
                panel.AttachedToVisualTree += OnAttached;
        });
    }

    private static void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is not Panel panel) return;
        int i = 0;
        foreach (var child in panel.Children)
        {
            var animation = new Animation
            {
                Duration = TimeSpan.FromSeconds(0.35),
                Delay = TimeSpan.FromSeconds(0.05 * i),
                Easing = new CubicEaseOut(),
                FillMode = FillMode.Both,
                Children =
                {
                    new KeyFrame { Cue = new Cue(0d), Setters = { new Setter(Visual.OpacityProperty, 0d) } },
                    new KeyFrame { Cue = new Cue(1d), Setters = { new Setter(Visual.OpacityProperty, 1d) } },
                },
            };
            _ = animation.RunAsync(child);
            i++;
        }
    }
}
