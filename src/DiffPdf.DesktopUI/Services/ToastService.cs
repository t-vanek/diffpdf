using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Threading;
using DiffPdf.DesktopUI.ViewModels;

namespace DiffPdf.DesktopUI.Services;

public enum ToastKind { Info, Success, Error }

/// <summary>A transient on-screen notification; auto-dismisses after a few seconds.</summary>
public sealed record ToastItem(string Message, ToastKind Kind)
{
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
    public ObservableCollection<ToastItem> Items { get; } = [];

    public void Show(string message, ToastKind kind = ToastKind.Info)
    {
        var toast = new ToastItem(message, kind);
        Items.Add(toast);
        // Auto-dismiss on the UI thread (Show is called after a UI-thread action completes).
        DispatcherTimer.RunOnce(() => Items.Remove(toast), TimeSpan.FromSeconds(3.5));
    }
}
