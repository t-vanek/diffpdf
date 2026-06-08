using DiffPdf.Application.Abstractions;
using DiffPdf.Application.Configuration;
using DiffPdf.Application.ControlChecks;
using DiffPdf.Application.Jobs;
using DiffPdf.Application.Queue;
using DiffPdf.Application.Runs;
using DiffPdf.Application.Scope;
using DiffPdf.Application.Stats;
using DiffPdf.Application.Subscriptions;
using DiffPdf.Application.Triggers;
using Microsoft.Extensions.DependencyInjection;

namespace DiffPdf.Application;

/// <summary>Registers the application-layer services (scope/job/check/subscription/trigger/config orchestration).</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds the application services. Scoped to match the per-request stores. The provisioner/resolver
    /// interfaces live in DiffPdf.Application.Abstractions (also consumed by the messaging hosted services);
    /// the implementations live here.
    /// </summary>
    public static IServiceCollection AddDiffPdfApplication(this IServiceCollection services)
    {
        // Scope management
        services.AddScoped<IBranchService, BranchService>();
        services.AddScoped<IInstanceService, InstanceService>();
        services.AddScoped<IScopeStatsService, ScopeStatsService>();

        // Jobs / runs / queue
        services.AddScoped<IJobService, JobService>();
        services.AddScoped<IScopeRunService, ScopeRunService>();
        services.AddScoped<IBranchQueueControl, BranchQueueControl>();

        // Automation: triggers, checks, subscriptions
        services.AddScoped<ITriggerService, TriggerService>();
        services.AddScoped<ITriggerProvisioner, TriggerProvisioner>();
        services.AddScoped<IControlCheckService, ControlCheckService>();
        services.AddScoped<ISubscriptionService, SubscriptionService>();

        // Per-scope configuration + schedules
        services.AddScoped<IScopeConfigurationService, ScopeConfigurationService>();
        services.AddScoped<IScopeConfigurationResolver, ScopeConfigurationResolver>();
        services.AddScoped<IScopeConfigurationProvisioner, ScopeConfigurationProvisioner>();
        services.AddScoped<IScheduleService, ScheduleService>();

        return services;
    }
}
