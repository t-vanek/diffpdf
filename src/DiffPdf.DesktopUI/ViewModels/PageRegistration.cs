using Microsoft.Extensions.DependencyInjection;

namespace DiffPdf.DesktopUI.ViewModels;

/// <summary>
/// Central registration of the nav section pages. Each section is registered both as its concrete
/// type (so pages can cross-navigate, e.g. a trigger jumping to Jobs) and as <see cref="PageViewModel"/>
/// (so the shell discovers it for the nav rail). Extended as sections are implemented.
/// </summary>
internal static class PageRegistration
{
    public static void Register(IServiceCollection services)
    {
        Add<DashboardViewModel>(services);
        Add<BranchesViewModel>(services);
        Add<InstancesViewModel>(services);
        Add<AutomationsViewModel>(services);
        Add<SubscriptionsViewModel>(services);
        Add<DiscoveryViewModel>(services);
        // "Spustit porovnání" page removed — comparisons are now launched from the per-row run-queue
        // controls on the Branches / Instances pages, so the standalone trigger page is redundant.
        Add<SingleCompareViewModel>(services);
        Add<JobsViewModel>(services);
        // Trigger + comparer configuration is no longer a central page; it lives per-scope behind the gear
        // button on each branch/instance, with the global level edited here.
        Add<SettingsViewModel>(services);

        // Content view-models hosted inside a section (not nav pages themselves).
        services.AddSingleton<ControlChecksViewModel>();
    }

    /// <summary>Registers <typeparamref name="T"/> as a singleton and exposes it as a nav <see cref="PageViewModel"/>.</summary>
    public static void Add<T>(IServiceCollection services) where T : PageViewModel
    {
        services.AddSingleton<T>();
        services.AddSingleton<PageViewModel>(sp => sp.GetRequiredService<T>());
    }
}
