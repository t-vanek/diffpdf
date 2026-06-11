using DiffPdf.Core.Abstractions;
using DiffPdf.Messaging.Messages;
using DiffPdf.Messaging.Observability;
using DiffPdf.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wolverine;

namespace DiffPdf.Messaging.Scheduling;

/// <summary>
/// Leader-gated safety net + restart recovery for the per-branch sequential queue. Every few seconds the
/// leader releases the next pending job for any branch that is idle and not held, so the queue keeps moving
/// even if a completion event was missed, and resumes automatically after a process restart. Reactive
/// dispatch (on enqueue / completion / cancel) keeps latency low; this timer guarantees eventual progress.
/// Mirrors <see cref="Automations.AutomationEngineService"/>'s leader-gating (same <see cref="AutomationLeader"/> lease).
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
    // A job whose pairs are all done but which is still Running this long after its last progress lost its
    // FinalizeBatch — re-publish it (idempotent). The grace avoids racing a finalize that is legitimately in flight.
    private static readonly TimeSpan FinalizeIdleGrace = TimeSpan.FromSeconds(60);
    // A Queued pair under an indexed Running job idle this long was very likely never dispatched (its
    // CompareFilePair publish was lost). Kept above the per-pair lease so a job that is merely slow-but-still-
    // -progressing (its UpdatedAt keeps advancing on each completion) is never swept.
    private static readonly TimeSpan StaleQueuedGrace = TimeSpan.FromMinutes(15);

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
        var jobs = scope.ServiceProvider.GetRequiredService<IJobStore>();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IBranchQueueDispatcher>();
        var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();
        var tasks = scope.ServiceProvider.GetRequiredService<IFilePairTaskStore>();

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

        // Re-finalize jobs that finished every pair but never got their FinalizeBatch (a crash between the
        // processed-count increment and the publish) — they would otherwise sit Running forever, blocking the
        // branch. Re-publishing is idempotent (FinalizeBatchHandler guards on Running + version).
        foreach (var pending in await jobs.ListRunningFullyProcessedAsync(DateTimeOffset.UtcNow - FinalizeIdleGrace, 50, ct))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await bus.PublishAsync(new FinalizeBatch(pending.Id));
                logger.LogWarning("Re-published lost FinalizeBatch for job {JobId} ({Branch}/{Instance}): {Processed}/{Total} done but still Running.",
                    pending.Id, pending.BranchKey, pending.InstanceKey, pending.ProcessedCount, pending.TotalCount);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { logger.LogWarning(ex, "Could not re-finalize job {JobId}.", pending.Id); }
        }

        // Re-dispatch Queued pairs stranded under a stale Running job — their CompareFilePair publish was lost
        // (after IndexBatch was acked), so no lease exists for the task sweeper to revive them and the job can
        // never reach Processed>=Total. Re-publishing is idempotent (the claim guard dedups a still-live pair).
        var stranded = await tasks.ListStaleQueuedAsync(DateTimeOffset.UtcNow - StaleQueuedGrace, 500, ct);
        foreach (var (jobId, taskId) in stranded)
        {
            ct.ThrowIfCancellationRequested();
            try { await bus.PublishAsync(new CompareFilePair(jobId, taskId)); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { logger.LogWarning(ex, "Could not re-dispatch stranded pair {TaskId}.", taskId); }
        }
        if (stranded.Count > 0)
        {
            // Flag the affected jobs so the client shows an "Obnoveno" chip (a lost-dispatch pair was recovered).
            await jobs.MarkRecoveredAsync(stranded.Select(s => s.JobId).Distinct().ToList(), ct);
            logger.LogWarning("Re-dispatched {Count} stranded Queued pair(s) under stale Running jobs.", stranded.Count);
        }

        // Advance only branches that actually have a pending job — one query instead of a dispatch probe per
        // branch (an idle branch has nothing to release or re-kick; reactive dispatch covers the rest).
        // Per-branch isolation: one failing branch never blocks the rest of the tick.
        foreach (var branchId in await jobs.ListBranchIdsWithPendingJobsAsync(ct))
        {
            ct.ThrowIfCancellationRequested();
            try { await dispatcher.DispatchBranchAsync(branchId, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { logger.LogWarning(ex, "Branch {BranchId} dispatch failed in tick.", branchId); }
        }
    }
}
