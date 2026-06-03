using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DiffPdf.Messaging.Scheduling;

public static class SchedulingServiceCollectionExtensions
{
    /// <summary>
    /// Registers the recurring-batch scheduler and the shared <see cref="IBatchLauncher"/>,
    /// bound from the <c>Schedules</c> configuration section. Safe to call always: when
    /// <c>Schedules:Enabled</c> is false the hosted service exits immediately.
    /// </summary>
    public static IServiceCollection AddDiffPdfScheduling(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ScheduleOptions>(configuration.GetSection(ScheduleOptions.SectionName));
        services.AddScoped<IBatchLauncher, BatchLauncher>();
        services.AddHostedService<ScheduledBatchService>();
        return services;
    }
}
