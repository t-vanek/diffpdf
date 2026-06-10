using System.Collections;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using DiffPdf.DesktopUI.ViewModels;

namespace DiffPdf.DesktopUI.Controls;

/// <summary>
/// Multi-select dropdown over <see cref="CheckableKey"/> items, with "empty selection = everything"
/// semantics: a combo-look trigger button carries the bound <see cref="Summary"/>, the flyout opens as the
/// field's expanded state (width matched to the button), explains itself via <see cref="Hint"/>, shows
/// <see cref="EmptyText"/> when there is nothing to offer, and ends with a single "Zrušit výběr" action.
/// <see cref="HasSelection"/> flips the trigger into the accent "restricted" look so an active scope
/// filter is visible at a glance.
/// </summary>
public partial class MultiSelectDropDown : UserControl
{
    public static readonly StyledProperty<IEnumerable?> ItemsSourceProperty =
        AvaloniaProperty.Register<MultiSelectDropDown, IEnumerable?>(nameof(ItemsSource));

    public static readonly StyledProperty<string?> SummaryProperty =
        AvaloniaProperty.Register<MultiSelectDropDown, string?>(nameof(Summary));

    public static readonly StyledProperty<string?> HintProperty =
        AvaloniaProperty.Register<MultiSelectDropDown, string?>(nameof(Hint));

    public static readonly StyledProperty<string?> EmptyTextProperty =
        AvaloniaProperty.Register<MultiSelectDropDown, string?>(nameof(EmptyText), "Žádné položky.");

    public static readonly StyledProperty<bool> HasSelectionProperty =
        AvaloniaProperty.Register<MultiSelectDropDown, bool>(nameof(HasSelection));

    public MultiSelectDropDown()
    {
        InitializeComponent();
        if (Trigger.Flyout is PopupFlyoutBase flyout)
            flyout.Opening += (_, _) => PrepareFlyout();
    }

    /// <summary>The offered items (<see cref="CheckableKey"/>); ticking is two-way via the item itself.</summary>
    public IEnumerable? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    /// <summary>Text shown on the trigger button (e.g. "Všechny větve" / "alfa, beta" / "Vybráno: 5").</summary>
    public string? Summary
    {
        get => GetValue(SummaryProperty);
        set => SetValue(SummaryProperty, value);
    }

    /// <summary>One-line explanation shown at the top of the flyout (the "empty = everything" semantics).</summary>
    public string? Hint
    {
        get => GetValue(HintProperty);
        set => SetValue(HintProperty, value);
    }

    /// <summary>Shown inside the flyout when <see cref="ItemsSource"/> offers nothing.</summary>
    public string? EmptyText
    {
        get => GetValue(EmptyTextProperty);
        set => SetValue(EmptyTextProperty, value);
    }

    /// <summary>True when anything is ticked — bound by the owner, drives the "restricted" accent look.</summary>
    public bool HasSelection
    {
        get => GetValue(HasSelectionProperty);
        set => SetValue(HasSelectionProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == HasSelectionProperty)
            Trigger.Classes.Set("restricted", change.GetNewValue<bool>());
    }

    /// <summary>The flyout reads as the opened state of the field: match its width to the trigger (the
    /// scopeFlyout presenter adds 6 px padding + 1 px border per side → 14 px) and reflect an empty offer.</summary>
    private void PrepareFlyout()
    {
        FlyoutRoot.MinWidth = Math.Max(0, Trigger.Bounds.Width - 14);
        EmptyLabel.IsVisible = ItemsSource is null || !ItemsSource.Cast<object>().Any();
    }

    private void OnClearClick(object? sender, RoutedEventArgs e)
    {
        if (ItemsSource is null) return;
        foreach (var item in ItemsSource.OfType<CheckableKey>())
            item.IsChecked = false;
    }
}
