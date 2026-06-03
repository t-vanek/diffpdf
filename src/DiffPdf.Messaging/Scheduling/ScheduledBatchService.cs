using DiffPdf.Core.Abstractions;
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
/// Multi-replica safe: each tick first acquires/renews the shared <see cref="AutomationLeader"/>
/// lease via <see cref="ILeaderElection"/>; only the leading replica reconciles and launches, so a
/// schedule fires once across the cluster (the in-memory fallback always leads). On leader death a
/// stand-by takes over within ~<see cref="AutomationLeader.Lease"/>; its reconciler re-seeds without
/// firing, so failover does not double-launch.
/// </remarks>
public sealed class ScheduledBatchService(
    IServiceScopeFactory scopeFactory,
    ILeaderElection leader,
    IWorkerInstanceIdProvider workerInstance,
    ILogger<ScheduledBatchService> logger) : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(20);

    private readonly ScheduleReconciler _reconciler = new();
    private bool _wasLeader;

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

    internal async Task TickAsync(CancellationToken ct)
    {
        // Only the automation leader reconciles + launches, so each schedule fires once across replicas.
        bool isLeader = await leader.TryAcquireAsync(AutomationLeader.Role, workerInstance.WorkerInstanceId, AutomationLeader.Lease, ct);
        if (isLeader != _wasLeader)
        {
            logger.LogInformation(isLeader
                ? "This replica is now the automation leader (scheduler active)."
                : "This replica is no longer the automation leader (scheduler idle).");
            _wasLeader = isLeader;
        }
        if (!isLeader)
            return;

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
                var result = await launcher.LaunchAsync(s.BranchKey, s.InstanceKey, LaunchSpec.FromSchedule(s), ct);
                if (result.JobId is { } id)
                {
                    // The launcher already recorded the schedule run (before publishing the command).
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
