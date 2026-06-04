using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DiffPdf.Messaging.ControlPlane;

public static class ControlPlaneServiceCollectionExtensions
{
    /// <summary>
    /// Registers the unified control-plane runner, its check executors, the runner service and the
    /// <see cref="IControlCheckProvisioner"/> that auto-creates the standard checks. Checks are runtime
    /// resources in <see cref="DiffPdf.Persistence.IControlCheckStore"/>; beyond the tick cadence and the
    /// auto-provision defaults there is nothing to configure.
    /// </summary>
    public static IServiceCollection AddDiffPdfControlPlane(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ControlPlaneOptions>(configuration.GetSection(ControlPlaneOptions.SectionName));

        services.AddScoped<IControlCheckExecutor, ReadinessCheckExecutor>();
        services.AddScoped<IControlCheckExecutor, HealthCheckExecutor>();
        services.AddScoped<IControlCheckExecutor, StructureSyncCheckExecutor>();
        services.AddScoped<IControlCheckExecutor, RetentionCheckExecutor>();

        services.AddScoped<IControlCheckRunner, ControlCheckRunner>();
        services.AddScoped<IControlCheckProvisioner, ControlCheckProvisioner>();
        services.AddHostedService<ControlPlaneService>();
        return services;
    }
}
