using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DiffPdf.Messaging.ScopeSync;

public static class ScopeSyncServiceCollectionExtensions
{
    /// <summary>
    /// Registers scope synchronization: the on-demand <see cref="IScopeSyncService"/> used by the
    /// <c>POST /api/v1/scope/sync</c> endpoint and the <c>StructureSync</c> control check. Periodic
    /// reconciliation now runs as a control check, not a dedicated background service.
    /// </summary>
    public static IServiceCollection AddDiffPdfScopeSync(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ScopeSyncOptions>(configuration.GetSection(ScopeSyncOptions.SectionName));
        services.AddScoped<IScopeSyncService, ScopeSyncService>();
        return services;
    }
}
