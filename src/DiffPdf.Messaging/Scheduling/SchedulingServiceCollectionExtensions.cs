using Microsoft.Extensions.DependencyInjection;

namespace DiffPdf.Messaging.Scheduling;

public static class SchedulingServiceCollectionExtensions
{
    /// <summary>
    /// Registers the DB-backed recurring-batch scheduler and the shared <see cref="IBatchLauncher"/>.
    /// The scheduler reads its schedules from <see cref="DiffPdf.Persistence.IScheduleStore"/> at runtime,
    /// so there is nothing to configure; it stays idle until schedules are created via the API.
    /// </summary>
    public static IServiceCollection AddDiffPdfScheduling(this IServiceCollection services)
    {
        services.AddScoped<IBatchLauncher, BatchLauncher>();
        services.AddHostedService<ScheduledBatchService>();
        return services;
    }
}
