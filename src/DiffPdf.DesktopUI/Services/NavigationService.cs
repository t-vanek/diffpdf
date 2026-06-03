using DiffPdf.DesktopUI.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace DiffPdf.DesktopUI.Services;

/// <summary>Lets one page open another (e.g. a trigger jumping to Jobs). The shell listens and switches.</summary>
public sealed class NavigationService(IServiceProvider provider)
{
    public event Action<PageViewModel>? Navigated;

    /// <summary>Resolves the target page (same singleton shown in the nav), configures it, then switches to it.</summary>
    public void GoTo<T>(Action<T>? configure = null) where T : PageViewModel
    {
        var page = provider.GetRequiredService<T>();
        configure?.Invoke(page);
        Navigated?.Invoke(page);
    }
}
