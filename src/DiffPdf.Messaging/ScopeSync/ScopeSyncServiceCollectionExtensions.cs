using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DiffPdf.Messaging.ScopeSync;

public static class ScopeSyncServiceCollectionExtensions
{
    /// <summary>
    /// Registers scope synchronization: the on-demand <see cref="IScopeSyncService"/> (always available)
    /// and the periodic/startup <see cref="ScopeSyncBackgroundService"/> (idle unless
    /// <c>ScopeSync:Enabled</c> is true and a root is configured).
    /// </summary>
    public static IServiceCollection AddDiffPdfScopeSync(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ScopeSyncOptions>(configuration.GetSection(ScopeSyncOptions.SectionName));
        services.AddScoped<IScopeSyncService, ScopeSyncService>();
        services.AddHostedService<ScopeSyncBackgroundService>();
        return services;
    }
}
