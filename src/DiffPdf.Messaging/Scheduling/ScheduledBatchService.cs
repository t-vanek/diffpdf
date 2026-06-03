using DiffPdf.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DiffPdf.Messaging.Scheduling;

/// <summary>
/// Fires due batches on their cron cadence (UTC). Every tick it re-reads the enabled schedules from
/// <see cref="IScheduleStore"/> (a fresh DI scope) and reconciles them through a
/// <see cref="ScheduleReconciler"/>, so schedules created/edited/deleted via the API take effect
/// within one tick — no restart. Idle (but alive) when the store has no enabled schedules.
/// </summary>
/// <remarks>
/// Single-process safe (the per-Id next-occurrence state prevents double firing). Running multiple
/// API replicas would fire each schedule once per replica — multi-replica single-fire (a DB
/// leader-lease) is the documented Phase-2 follow-up.
/// </remarks>
public sealed class ScheduledBatchService(
    IServiceScopeFactory scopeFactory,
    ILogger<ScheduledBatchService> logger) : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(20);

    private readonly ScheduleReconciler _reconciler = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Batch scheduler started (DB-backed, {Interval}s tick).", TickInterval.TotalSeconds);

        using var timer = new PeriodicTimer(TickInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Scheduler tick failed.");
            }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IScheduleStore>();
        var launcher = scope.ServiceProvider.GetRequiredService<IBatchLauncher>();

        var schedules = await store.ListEnabledAsync(ct);
        var due = _reconciler.Reconcile(
            DateTime.UtcNow, schedules,
            (s, ex) => logger.LogError(ex, "Ignoring schedule {Branch}/{Instance}/{Key}: invalid cron '{Cron}'.",
                s.BranchKey, s.InstanceKey, s.Key, s.Cron));

        foreach (var s in due)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var jobId = await launcher.LaunchAsync(s.BranchKey, s.InstanceKey, LaunchSpec.FromSchedule(s), ct);
                if (jobId is { } id)
                {
                    await store.TouchLastRunAsync(s.Id, DateTimeOffset.UtcNow, ct);
                    logger.LogInformation("Scheduled batch {JobId} launched for {Branch}/{Instance}/{Key}.",
                        id, s.BranchKey, s.InstanceKey, s.Key);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Scheduled launch for {Branch}/{Instance}/{Key} failed.", s.BranchKey, s.InstanceKey, s.Key);
            }
        }
    }
}
