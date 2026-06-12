using DiffPdf.Core.Abstractions;
using DiffPdf.Core.Comparison;
using DiffPdf.Core.Models;
using DiffPdf.Messaging.Messages;
using DiffPdf.Messaging.Observability;
using DiffPdf.Persistence;
using Microsoft.Extensions.Logging;
using Wolverine;

namespace DiffPdf.Messaging.Handlers;

/// <summary>Pairs the job's folders into file-pair tasks, sets the total, and dispatches a task each.</summary>
public sealed class IndexBatchHandler
{
    /// <summary>
    /// Returns a <see cref="BatchFailed"/> event (cascaded by Wolverine) when indexing hard-fails,
    /// so a <c>Failed</c> notification is raised; null on the normal path.
    /// </summary>
    public static async Task<BatchFailed?> Handle(
        IndexBatch command,
        IJobStore jobStore,
        IFilePairTaskStore taskStore,
        IStorageProvisioner provisioner,
        ITriggerEventPublisher triggerEvents,
        IMessageBus bus,
        DiffPdfMetrics metrics,
        ILogger<IndexBatchHandler> logger,
        CancellationToken ct)
    {
        using var logScope = logger.BeginScope(new Dictionary<string, object> { ["JobId"] = command.JobId });

        var job = await jobStore.GetAsync(command.JobId, ct);
        if (job is null || job.Status != JobStatus.Running)
        {
            logger.LogInformation("IndexBatch skipped for {JobId} (status {Status}).", command.JobId, job?.Status);
            return null;
        }

        // Idempotency. IndexBatch is a durable, at-least-once message: it can be redelivered (the first run
        // committed its tasks but threw — or was killed — before acking, or any double dispatch). The job is
        // still Running in that window, so the status check above does not catch it. Indexing again would
        // create a SECOND full set of tasks with fresh ids, corrupting the processed/total accounting
        // (SetTotalAsync says N, but 2N tasks complete) and double-triggering FinalizeBatch. So if this job
        // already has file-pair tasks it was already indexed: never create a second set. Instead re-dispatch
        // any pairs still Queued — covering a first run that committed the tasks but did not publish every
        // CompareFilePair before it died — and stop. Re-publishing CompareFilePair for an already-claimed or
        // finished pair is a no-op (TryClaimAsync only succeeds on a Queued task); this mirrors how
        // JobResumeService re-dispatches and StaleTaskRecoveryService requeues, all of which key off existing
        // task ids rather than minting new ones, so they never conflict.
        var existing = await taskStore.ListByJobAsync(job.Id, ct);
        if (existing.Count > 0)
        {
            var pending = existing.Where(t => t.Status == FilePairTaskStatus.Queued).ToList();
            foreach (var t in pending)
                await bus.PublishAsync(new CompareFilePair(job.Id, t.Id));

            // Every pair already reached a terminal state but the job is still Running — a lost FinalizeBatch.
            // Nudge finalization (idempotent). Guarded on all-terminal so we never finalize while pairs are
            // still Queued/Running: those drive finalization themselves (in-flight completion, or the
            // stale-lease sweeper re-dispatching a crashed worker's pair).
            if (existing.All(t => t.Status is FilePairTaskStatus.Completed or FilePairTaskStatus.Failed or FilePairTaskStatus.Skipped))
                await bus.PublishAsync(new FinalizeBatch(job.Id));

            logger.LogInformation(
                "IndexBatch for already-indexed job {JobId} ({Existing} task(s)); re-dispatched {Pending} queued pair(s).",
                job.Id, existing.Count, pending.Count);
            return null;
        }

        await provisioner.EnsureJobFoldersAsync(job, ct);

        IReadOnlyList<FilePair> pairs;
        try
        {
            var req = job.Request;
            pairs = FolderPairing.Pair(req.OldFolder, req.NewFolder, req.SearchPattern, req.Recursive);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Indexing failed for job {JobId}", job.Id);
            var now = DateTimeOffset.UtcNow;
            await jobStore.FailAsync(job.Id, ex.Message, job.Version, ct);
            metrics.RecordJobFinished("failed", now - (job.StartedAt ?? job.CreatedAt));

            // Best-effort realtime push: a throw after the FailAsync commit would make the Wolverine retry
            // see a non-Running job and return null — losing the BatchFailed cascade (the Failed
            // notification) forever. CancellationToken.None so a shutdown mid-failure cannot abort it either.
            try
            {
                // Real-time comparison.failed for trigger-launched batches (after the failure is committed).
                if (job.TriggerId is { } triggerId)
                    await triggerEvents.PublishAsync(new TriggerEvent(
                        "comparison.failed", triggerId, job.Id, job.BranchId, job.InstanceId, job.BranchKey, job.InstanceKey,
                        Status: "failed", Result: "error", StartedAt: job.StartedAt, FinishedAt: now,
                        Source: job.Source.ToString(), Message: "Porovnání selhalo.", ErrorMessage: ex.Message), CancellationToken.None);
            }
            catch (Exception pushEx)
            {
                logger.LogWarning(pushEx, "Job {JobId}: realtime publish after failure failed; continuing the cascade.", job.Id);
            }

            return new BatchFailed(job.Id, job.BranchKey, job.InstanceKey, ex.Message, now, job.SourceAutomationId);
        }

        await jobStore.SetTotalAsync(job.Id, pairs.Count, ct);

        if (pairs.Count == 0)
        {
            await bus.PublishAsync(new FinalizeBatch(job.Id));
            return null;
        }

        var tasks = pairs.Select(p => new FilePairTask
        {
            Id = Guid.NewGuid(),
            JobId = job.Id,
            RelativePath = p.RelativePath,
            OldFilePath = p.OldPath,
            NewFilePath = p.NewPath,
        }).ToList();

        await taskStore.CreateManyAsync(tasks, ct);

        foreach (var task in tasks)
            await bus.PublishAsync(new CompareFilePair(job.Id, task.Id));

        logger.LogInformation("Job {JobId} indexed into {Count} file-pair tasks.", job.Id, tasks.Count);
        return null;
    }
}
