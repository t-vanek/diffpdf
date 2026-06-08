using CommunityToolkit.Mvvm.ComponentModel;
using DiffPdf.Client;
using DiffPdf.DesktopUI.Services;

namespace DiffPdf.DesktopUI.ViewModels;

/// <summary>Base for all view-models: busy flag + error surface + a guarded async runner.</summary>
public abstract partial class ViewModelBase : ObservableObject
{
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _error;

    /// <summary>Optional global toast sink (set once at startup). A failed action also raises its error as a toast,
    /// so it's noticed even if the user isn't looking at the page header. Null in tests → no-op.</summary>
    public static ToastService? ToastSink { get; set; }

    /// <summary>Runs an async action with busy-gating and unified error handling (API errors show status + detail).
    /// A failure surfaces its error as a toast unless <paramref name="toastOnError"/> is false — set that for
    /// periodic background refreshes so a persistent failure doesn't spam a toast every few seconds.</summary>
    protected async Task RunAsync(Func<Task> action, bool toastOnError = true)
    {
        IsBusy = true;
        Error = null;
        try
        {
            await action();
        }
        catch (DiffPdfApiException ex)
        {
            Error = $"{(int)ex.StatusCode} {ex.StatusCode}: {ex.Detail ?? ex.Message}";
        }
        catch (Exception ex) when (IsConnectionError(ex))
        {
            Error = "Server není dostupný — zkontroluj, že běží, a ověř adresu (⚙ vpravo nahoře).";
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
        if (toastOnError && Error is { } error) ToastSink?.Show(error, ToastKind.Error);
    }

    /// <summary>True for transport-level failures (server down / wrong address / timeout) — as opposed to a
    /// server-returned <see cref="DiffPdfApiException"/>. Lets us show a clear "server unreachable" message
    /// instead of a raw socket error, which otherwise leaves the user unsure if it's their fault or the server's.</summary>
    private static bool IsConnectionError(Exception ex) =>
        ex is System.Net.Http.HttpRequestException
            or System.Net.Sockets.SocketException
            or TaskCanceledException
            or TimeoutException
        || (ex.InnerException is { } inner && IsConnectionError(inner));
}

/// <summary>A navigable section page shown in the left nav rail.</summary>
public abstract class PageViewModel : ViewModelBase
{
    /// <summary>Label in the nav rail.</summary>
    public abstract string Title { get; }

    /// <summary>Glyph shown before the title in the nav rail.</summary>
    public virtual string Icon => "•";

    /// <summary>Sort order in the nav rail.</summary>
    public abstract int NavOrder { get; }

    /// <summary>Called when the page becomes visible (and the app is connected) — lazy load.</summary>
    public virtual Task ActivateAsync() => Task.CompletedTask;

    /// <summary>Called when navigating away — release per-page realtime subscriptions / background work.</summary>
    public virtual Task DeactivateAsync() => Task.CompletedTask;
}
