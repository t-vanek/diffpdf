using Cronos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DiffPdf.Messaging.Scheduling;

/// <summary>
/// Fires recurring batches on their cron cadence (UTC). Parses each schedule's cron once,
/// then on a short timer launches any that have come due via <see cref="IBatchLauncher"/>.
/// </summary>
/// <remarks>
/// Single-process safe (an in-memory "next occurrence" per schedule prevents double firing).
/// Running multiple API replicas would fire each schedule once per replica — multi-replica
/// single-fire (a DB leader-lease) is the documented Phase-2 follow-up.
/// </remarks>
public sealed class ScheduledBatchService(
    IServiceScopeFactory scopeFactory,
    IOptions<ScheduleOptions> options,
    ILogger<ScheduledBatchService> logger) : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(20);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = options.Value;
        if (!opts.Enabled || opts.Jobs.Count == 0)
        {
            logger.LogInformation("Batch scheduler disabled or no schedules configured.");
            return;
        }

        var entries = new List<(ScheduledBatch Def, CronExpression Cron, DateTime? Next)>();
        foreach (var job in opts.Jobs)
        {
            if (!job.Enabled)
                continue;
            try
            {
                var cron = CronExpression.Parse(job.Cron);
                entries.Add((job, cron, cron.GetNextOccurrence(DateTime.UtcNow)));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Ignoring schedule for {Branch}/{Instance}: invalid cron '{Cron}'.",
                    job.BranchKey, job.InstanceKey, job.Cron);
            }
        }

        if (entries.Count == 0)
        {
            logger.LogWarning("Batch scheduler enabled but no valid schedules to run.");
            return;
        }

        logger.LogInformation("Batch scheduler started with {Count} schedule(s).", entries.Count);

        using var timer = new PeriodicTimer(TickInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var now = DateTime.UtcNow;
            for (int i = 0; i < entries.Count; i++)
            {
                var (def, cron, next) = entries[i];
                if (next is not { } due || now < due)
                    continue;

                try
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var launcher = scope.ServiceProvider.GetRequiredService<IBatchLauncher>();
                    await launcher.LaunchAsync(def.BranchKey, def.InstanceKey, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Scheduled launch for {Branch}/{Instance} failed.", def.BranchKey, def.InstanceKey);
                }

                entries[i] = (def, cron, cron.GetNextOccurrence(now));
            }
        }
    }
}
