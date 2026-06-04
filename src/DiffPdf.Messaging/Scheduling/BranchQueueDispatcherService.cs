using DiffPdf.Core.Abstractions;
using DiffPdf.Messaging.Observability;
using DiffPdf.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DiffPdf.Messaging.Scheduling;

/// <summary>
/// Leader-gated safety net + restart recovery for the per-branch sequential queue. Every few seconds the
/// leader releases the next pending job for any branch that is idle and not held, so the queue keeps moving
/// even if a completion event was missed, and resumes automatically after a process restart. Reactive
/// dispatch (on enqueue / completion / cancel) keeps latency low; this timer guarantees eventual progress.
/// Mirrors <see cref="ControlPlane.ControlPlaneService"/>'s leader-gating (same <see cref="AutomationLeader"/> lease).
/// </summary>
public sealed class BranchQueueDispatcherService(
    IServiceScopeFactory scopeFactory,
    ILeaderElection leader,
    IWorkerInstanceIdProvider workerInstance,
    IAutomationHeartbeat heartbeat,
    DiffPdfMetrics metrics,
    ILogger<BranchQueueDispatcherService> logger) : BackgroundService
{
    private const string ServiceName = "branch-queue";
    // Reactive dispatch (enqueue / completion / cancel / resume) handles latency; this is the catch-up + restart-recovery net.
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(7);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Branch-queue dispatcher started ({Interval}s tick).", Interval.TotalSeconds);
        using var timer = new PeriodicTimer(Interval);
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
                heartbeat.Record(ServiceName, false, ex.Message);
                logger.LogError(ex, "Branch-queue dispatch tick failed.");
            }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        bool isLeader = await leader.TryAcquireAsync(AutomationLeader.Role, workerInstance.WorkerInstanceId, AutomationLeader.Lease, ct);
        heartbeat.Record(ServiceName, isLeader);
        if (!isLeader)
            return;

        await using var scope = scopeFactory.CreateAsyncScope();
        var branches = scope.ServiceProvider.GetRequiredService<IBranchStore>();
        var jobs = scope.ServiceProvider.GetRequiredService<IJobStore>();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IBranchQueueDispatcher>();

        // Recover jobs stuck Running (crashed during indexing → no file-pair tasks for task recovery to revive),
        // which would otherwise occupy the branch forever; fail them so the queue advances.
        foreach (var stuck in await jobs.ListStaleUnindexedRunningAsync(DateTimeOffset.UtcNow, 50, ct))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await jobs.FailAsync(stuck.Id, "Worker lease expired before indexing completed (stale job).", stuck.Version, ct);
                metrics.RecordJobFinished("failed", DateTimeOffset.UtcNow - (stuck.StartedAt ?? stuck.CreatedAt));
                logger.LogWarning("Recovered stale job {JobId} for {Branch}/{Instance} (expired lease, never indexed).",
                    stuck.Id, stuck.BranchKey, stuck.InstanceKey);
                await dispatcher.DispatchBranchAsync(stuck.BranchId, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { logger.LogWarning(ex, "Could not recover stale job {JobId}.", stuck.Id); }
        }

        // Advance every branch (per-branch isolation: one failing branch never blocks the rest of the tick).
        foreach (var branch in await branches.ListAsync(ct))
        {
            ct.ThrowIfCancellationRequested();
            try { await dispatcher.DispatchBranchAsync(branch.Id, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { logger.LogWarning(ex, "Branch {Branch} dispatch failed in tick.", branch.Key); }
        }
    }
}
