using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DiffPdf.Messaging.ControlPlane;

public static class ControlPlaneServiceCollectionExtensions
{
    /// <summary>
    /// Registers the unified control-plane runner, its check executors and the runner service. Checks are
    /// runtime resources in <see cref="DiffPdf.Persistence.IControlCheckStore"/>, so there is nothing to
    /// configure beyond the tick cadence; the runner stays idle until checks are created via the API.
    /// </summary>
    public static IServiceCollection AddDiffPdfControlPlane(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ControlPlaneOptions>(configuration.GetSection(ControlPlaneOptions.SectionName));

        services.AddScoped<IControlCheckExecutor, ReadinessCheckExecutor>();
        services.AddScoped<IControlCheckExecutor, HealthCheckExecutor>();
        services.AddScoped<IControlCheckExecutor, StructureSyncCheckExecutor>();
        services.AddScoped<IControlCheckExecutor, RetentionCheckExecutor>();

        services.AddScoped<IControlCheckRunner, ControlCheckRunner>();
        services.AddHostedService<ControlPlaneService>();
        return services;
    }
}
