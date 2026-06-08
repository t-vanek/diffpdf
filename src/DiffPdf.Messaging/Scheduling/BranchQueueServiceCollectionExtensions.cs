using DiffPdf.Core.Abstractions;
using DiffPdf.Messaging.Observability;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DiffPdf.Messaging.Scheduling;

public static class BranchQueueServiceCollectionExtensions
{
    /// <summary>
    /// Registers the per-branch sequential queue: the dispatcher, the leader-gated catch-up timer, and a
    /// no-op queue-state publisher fallback (the API overrides it with the SignalR publisher).
    /// </summary>
    public static IServiceCollection AddDiffPdfBranchQueue(this IServiceCollection services)
    {
        services.AddSingleton<BranchDispatchLocks>();
        services.AddSingleton<DiffPdfMetrics>();
        services.AddScoped<IJobResumeService, JobResumeService>();
        services.AddScoped<IRunCommandPublisher, WolverineRunCommandPublisher>();
        services.AddScoped<IBranchQueueDispatcher, BranchQueueDispatcher>();
        services.TryAddSingleton<IBranchQueueStatePublisher, NullBranchQueueStatePublisher>();
        services.AddHostedService<BranchQueueDispatcherService>();
        return services;
    }
}
