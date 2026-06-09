using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using DiffPdf.DesktopUI.ViewModels;

namespace DiffPdf.DesktopUI.Views;

public partial class MainWindow : Window
{
    // Tint opacity over the Mica backdrop: high enough to preserve the dark design identity and text contrast,
    // low enough to let the Win11 material breathe. Tweak to taste.
    private const double MicaTintOpacity = 0.88;

    // The page selected when a nav press begins — captured before the ListBox updates its selection, so OnNavTapped
    // can tell a re-click on the active item (→ reload) from switching pages (→ the selection change activates it).
    private PageViewModel? _navPageBeforePress;

    public MainWindow()
    {
        InitializeComponent();
        // Capture the selection on the way down (tunnel, before the item updates it); decide on the way up (tap).
        NavList.AddHandler(PointerPressedEvent, OnNavPointerPressed, RoutingStrategies.Tunnel);
        NavList.Tapped += OnNavTapped;
    }

    private void OnNavPointerPressed(object? sender, PointerPressedEventArgs e)
        => _navPageBeforePress = (DataContext as MainViewModel)?.SelectedPage;

    // Re-clicking the already-active nav item reloads its page (the per-page "Obnovit" button is gone). Switching
    // pages needs nothing here — the selection change already activates (loads) the target.
    private void OnNavTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not MainViewModel { SelectedPage: { } page } || !ReferenceEquals(page, _navPageBeforePress))
            return;
        if ((e.Source as Control)?.DataContext is PageViewModel tapped && ReferenceEquals(tapped, page))
            _ = page.ReloadAsync();
    }

    // Title-bar content opacity while the window is inactive (no focus) — a subtle dim, like a native title bar.
    private const double InactiveTitleOpacity = 0.5;

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        UpdateMicaTint();
        UpdateMaximizeRestoreIcon();
        UpdateActiveDim();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ActualTransparencyLevelProperty)
            UpdateMicaTint();
        else if (change.Property == WindowStateProperty)
            UpdateMaximizeRestoreIcon();
        else if (change.Property == IsActiveProperty)
            UpdateActiveDim();
    }

    /// <summary>When the OS grants a backdrop (Mica / Acrylic / Blur) the tint stays semi-transparent so the
    /// material shows; when it doesn't (e.g. Windows 10) the tint goes fully opaque so the window isn't
    /// see-through to the desktop.</summary>
    private void UpdateMicaTint()
    {
        if (MicaTint is { } tint)
            tint.Opacity = ActualTransparencyLevel == WindowTransparencyLevel.None ? 1.0 : MicaTintOpacity;
    }

    /// <summary>Show the "restore" (overlapping squares) glyph while maximized and the "maximize" (single square)
    /// glyph otherwise — Win11 convention. Driven by WindowState so it tracks every path to maximize (the button,
    /// double-tapping the title bar, OS snap, Win+Up). Null-guarded: property changes can fire before the template's
    /// named parts exist.</summary>
    private void UpdateMaximizeRestoreIcon()
    {
        bool maximized = WindowState == WindowState.Maximized;
        if (MaxGlyph is { } max) max.IsVisible = !maximized;
        if (RestoreGlyph is { } restore) restore.IsVisible = maximized;
    }

    /// <summary>Dim the whole title-bar content when the window loses focus (and restore it on activation), matching
    /// how native title bars fade while inactive. The Opacity transition on the grid makes the change smooth.</summary>
    private void UpdateActiveDim()
    {
        if (TitleBarContent is { } content)
            content.Opacity = IsActive ? 1.0 : InactiveTitleOpacity;
    }

    // ----- Custom title bar (WindowDecorations=BorderOnly): we own drag + the caption buttons. -----

    /// <summary>Drag the window from the title bar. Clicks on the gear / caption buttons handle their own press
    /// (marking it handled), so this only fires on the title text and the empty gap.</summary>
    private void OnTitleBarPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void OnTitleBarDoubleTapped(object? sender, TappedEventArgs e) => ToggleMaximize();

    private void OnMinimize(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeRestore(object? sender, RoutedEventArgs e) => ToggleMaximize();

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
}
