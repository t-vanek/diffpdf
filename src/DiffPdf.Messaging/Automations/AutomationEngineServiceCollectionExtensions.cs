using DiffPdf.Application.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DiffPdf.Messaging.Automations;

public static class AutomationEngineServiceCollectionExtensions
{
    /// <summary>
    /// Registers the automation engine: the scheduler service, the run pipeline, the step executors, the
    /// event sink and the <see cref="IAutomationProvisioner"/> that auto-creates the standard automations.
    /// Automations are runtime resources in <see cref="DiffPdf.Persistence.IAutomationStore"/>; beyond the
    /// tick cadence, the run parallelism and the auto-provision defaults there is nothing to configure.
    /// Binds the <c>Automation</c> section, falling back to the legacy <c>ControlPlane</c> section so
    /// existing deployments keep their configured cadences without an appsettings edit.
    /// </summary>
    public static IServiceCollection AddDiffPdfAutomationEngine(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(AutomationEngineOptions.SectionName);
        if (!section.Exists())
            section = configuration.GetSection(AutomationEngineOptions.LegacySectionName);
        services.Configure<AutomationEngineOptions>(section);

        services.AddScoped<IAutomationStepExecutor, ReadinessStepExecutor>();
        services.AddScoped<IAutomationStepExecutor, HealthStepExecutor>();
        services.AddScoped<IAutomationStepExecutor, StructureSyncStepExecutor>();
        services.AddScoped<IAutomationStepExecutor, RetentionStepExecutor>();
        services.AddScoped<IAutomationStepExecutor, DbRowRetentionStepExecutor>();
        services.AddScoped<IAutomationStepExecutor, ScheduledComparisonStepExecutor>();
        services.AddScoped<IAutomationStepExecutor, SystemResourceStepExecutor>();
        services.AddScoped<IAutomationStepExecutor, QueueHealthStepExecutor>();
        services.AddScoped<IAutomationStepExecutor, ComparisonFreshnessStepExecutor>();

        services.AddScoped<IAutomationRunner, AutomationRunner>();
        services.AddScoped<IAutomationProvisioner, AutomationProvisioner>();
        services.AddScoped<IAutomationEventSink, AutomationEventSink>();
        services.AddSingleton<AutomationMetrics>();
        services.AddHostedService<AutomationEngineService>();
        return services;
    }
}
