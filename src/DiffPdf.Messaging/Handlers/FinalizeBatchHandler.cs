using DiffPdf.Core.Abstractions;
using DiffPdf.Core.Models;
using DiffPdf.Core.Storage;
using DiffPdf.Messaging.Messages;
using DiffPdf.Messaging.Observability;
using DiffPdf.Persistence;
using Microsoft.Extensions.Logging;

namespace DiffPdf.Messaging.Handlers;

/// <summary>Aggregates file-pair results into the batch report and completes the job (idempotent).</summary>
public sealed class FinalizeBatchHandler
{
    /// <summary>
    /// Returns a <see cref="BatchFinished"/> event (cascaded by Wolverine) when this call is the
    /// one that completes the job, or null when the job was already finalized / not ready.
    /// </summary>
    public static async Task<BatchFinished?> Handle(
        FinalizeBatch command,
        IJobStore jobStore,
        IFilePairTaskStore taskStore,
        IJobProgressPublisher progressPublisher,
        ITriggerEventPublisher triggerEvents,
        DiffPdfMetrics metrics,
        ILogger<FinalizeBatchHandler> logger,
        CancellationToken ct)
    {
        var job = await jobStore.GetAsync(command.JobId, ct);
        if (job is null || job.Status != JobStatus.Running)
            return null; // already finalized or not ready

        var tasks = await taskStore.ListByJobAsync(command.JobId, ct);
        var files = tasks
            .Select(t => t.Result ?? new FilePairResult
            {
                RelativePath = t.RelativePath,
                Status = FilePairStatus.Error,
                Error = t.Error ?? "not processed",
            })
            .ToList();

        var report = new BatchComparisonReport
        {
            OldFolder = job.Request.OldFolder,
            NewFolder = job.Request.NewFolder,
            StartedAt = job.StartedAt ?? job.CreatedAt,
            CompletedAt = DateTimeOffset.UtcNow,
            Files = files,
            Gate = job.Request.Gate,
        };

        try
        {
            // This is the terminal job-completion write. It must run to completion even if ct is cancelling
            // (host shutdown; there is no Wolverine message timeout): all pairs are already done, so abandoning
            // it would strand the job in Running with no recovery — the lease sweeper only requeues stale
            // tasks, not finalize-pending jobs. Use CancellationToken.None so the completion (and the
            // BatchFinished cascade it enables) is never lost mid-finalize.
            var completed = await jobStore.CompleteAsync(job.Id, report, job.Version, CancellationToken.None);
            await progressPublisher.PublishAsync(JobProgressChanged.From(completed), CancellationToken.None);
            metrics.RecordJobFinished(report.Passed ? "passed" : "gate_violated", report.CompletedAt - report.StartedAt);

            // Real-time comparison.completed for trigger-launched batches (after the completion is committed).
            if (completed.TriggerId is { } triggerId)
                await triggerEvents.PublishAsync(new TriggerEvent(
                    "comparison.completed", triggerId, completed.Id, completed.BranchId, completed.InstanceId,
                    completed.BranchKey, completed.InstanceKey,
                    Status: "completed", Result: report.Passed ? "success" : "gate-violated",
                    StartedAt: report.StartedAt, FinishedAt: report.CompletedAt,
                    DurationMs: (long)(report.CompletedAt - report.StartedAt).TotalMilliseconds,
                    Source: completed.Source.ToString(), Message: "Porovnání bylo dokončeno.",
                    ResultReference: completed.Id.ToString()), CancellationToken.None);

            logger.LogInformation("Job {JobId} finalized: {Total} files, {Diff} differing.",
                completed.Id, report.Total, report.Differing);

            return new BatchFinished(
                completed.Id, job.BranchKey, job.InstanceKey,
                report.Total, report.Identical, report.Differing, report.Errors,
                report.FilesWithContentErrors, report.Passed,
                report.GateViolations.ToArray(), report.CompletedAt);
        }
        catch (ConcurrencyConflictException)
        {
            logger.LogInformation("Job {JobId} already finalized.", job.Id);
            return null;
        }
    }
}
