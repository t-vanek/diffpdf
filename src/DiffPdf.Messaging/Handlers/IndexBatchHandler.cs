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
        var job = await jobStore.GetAsync(command.JobId, ct);
        if (job is null || job.Status != JobStatus.Running)
        {
            logger.LogInformation("IndexBatch skipped for {JobId} (status {Status}).", command.JobId, job?.Status);
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

            // Real-time comparison.failed for trigger-launched batches (after the failure is committed).
            if (job.TriggerId is { } triggerId)
                await triggerEvents.PublishAsync(new TriggerEvent(
                    "comparison.failed", triggerId, job.Id, job.BranchId, job.InstanceId, job.BranchKey, job.InstanceKey,
                    Status: "failed", Result: "error", StartedAt: job.StartedAt, FinishedAt: now,
                    Source: job.Source.ToString(), Message: "Porovnání selhalo.", ErrorMessage: ex.Message), ct);

            return new BatchFailed(job.Id, job.BranchKey, job.InstanceKey, ex.Message, now);
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
