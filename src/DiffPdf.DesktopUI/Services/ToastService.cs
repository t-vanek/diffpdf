using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using DiffPdf.DesktopUI.ViewModels;

namespace DiffPdf.DesktopUI.Services;

public enum ToastKind { Info, Success, Error }

/// <summary>A transient on-screen notification; auto-dismisses after a few seconds.</summary>
public sealed partial class ToastItem : ObservableObject
{
    public ToastItem(string message, ToastKind kind) => (Message, Kind) = (message, kind);

    public string Message { get; }
    public ToastKind Kind { get; }

    /// <summary>Flipped shortly before removal — the view's "closing" class fades the card out.</summary>
    [ObservableProperty] private bool _isClosing;

    /// <summary>Accent colour (left bar + border) by kind.</summary>
    public IBrush Accent => Kind switch
    {
        ToastKind.Success => Palette.Good,
        ToastKind.Error => Palette.Bad,
        _ => Palette.Info,
    };
}

/// <summary>
/// Shows transient toast notifications in the shell (bottom-right), each auto-removed after a few seconds.
/// A single shared instance backs the overlay (<see cref="MainViewModel"/>.Toasts) and is raised from actions
/// via <see cref="DialogService.ShowToast"/>.
/// </summary>
public sealed class ToastService
{
    // Errors linger twice as long — they carry detail the user has to actually read, while a
    // success/info toast just confirms what they did a moment ago.
    private static readonly TimeSpan InfoLifetime = TimeSpan.FromSeconds(3.5);
    private static readonly TimeSpan ErrorLifetime = TimeSpan.FromSeconds(7);
    private static readonly TimeSpan FadeOut = TimeSpan.FromMilliseconds(280);

    public ObservableCollection<ToastItem> Items { get; } = [];

    public void Show(string message, ToastKind kind = ToastKind.Info)
    {
        var toast = new ToastItem(message, kind);
        Items.Add(toast);
        // Auto-dismiss on the UI thread (Show is called after a UI-thread action completes):
        // first start the fade-out, then drop the item once the fade has played.
        var lifetime = kind == ToastKind.Error ? ErrorLifetime : InfoLifetime;
        DispatcherTimer.RunOnce(() => toast.IsClosing = true, lifetime);
        DispatcherTimer.RunOnce(() => Items.Remove(toast), lifetime + FadeOut);
    }
}
