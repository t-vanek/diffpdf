using CommunityToolkit.Mvvm.ComponentModel;
using DiffPdf.Client;

namespace DiffPdf.DesktopUI.ViewModels;

/// <summary>Base for all view-models: busy flag + error surface + a guarded async runner.</summary>
public abstract partial class ViewModelBase : ObservableObject
{
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _error;

    /// <summary>Runs an async action with busy-gating and unified error handling (API errors show status + detail).</summary>
    protected async Task RunAsync(Func<Task> action)
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
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }
}

/// <summary>A navigable section page shown in the left nav rail.</summary>
public abstract class PageViewModel : ViewModelBase
{
    /// <summary>Label in the nav rail.</summary>
    public abstract string Title { get; }

    /// <summary>Sort order in the nav rail.</summary>
    public abstract int NavOrder { get; }

    /// <summary>Called when the page becomes visible (and the app is connected) — lazy load.</summary>
    public virtual Task ActivateAsync() => Task.CompletedTask;
}
