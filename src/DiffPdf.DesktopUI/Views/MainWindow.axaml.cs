using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace DiffPdf.DesktopUI.Views;

public partial class MainWindow : Window
{
    // Tint opacity over the Mica backdrop: high enough to preserve the dark design identity and text contrast,
    // low enough to let the Win11 material breathe. Tweak to taste.
    private const double MicaTintOpacity = 0.88;

    public MainWindow() => InitializeComponent();

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        UpdateMicaTint();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ActualTransparencyLevelProperty)
            UpdateMicaTint();
    }

    /// <summary>When the OS grants a backdrop (Mica / Acrylic / Blur) the tint stays semi-transparent so the
    /// material shows; when it doesn't (e.g. Windows 10) the tint goes fully opaque so the window isn't
    /// see-through to the desktop.</summary>
    private void UpdateMicaTint()
    {
        if (MicaTint is { } tint)
            tint.Opacity = ActualTransparencyLevel == WindowTransparencyLevel.None ? 1.0 : MicaTintOpacity;
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
