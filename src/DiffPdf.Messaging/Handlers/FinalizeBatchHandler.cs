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
        ISystemEventLog systemEvents,
        DiffPdfMetrics metrics,
        ILogger<FinalizeBatchHandler> logger,
        CancellationToken ct)
    {
        using var logScope = logger.BeginScope(new Dictionary<string, object> { ["JobId"] = command.JobId });

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
            metrics.RecordJobFinished(report.Passed ? "passed" : "gate_violated", report.CompletedAt - report.StartedAt);

            // Realtime pushes are best-effort and must never abort the handler past the terminal commit:
            // a throw here would make Wolverine redeliver FinalizeBatch, the retry would see a non-Running
            // job and return null, and the BatchFinished cascade (notifications, queue advance) would be
            // lost forever. The pushes have no backstop of their own — clients reload from REST.
            try
            {
                await progressPublisher.PublishAsync(JobProgressChanged.From(completed), CancellationToken.None);

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
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Job {JobId}: realtime publish after completion failed; continuing the cascade.", job.Id);
            }

            // Durable trail (+ notification-center push); AppendAsync is best-effort by contract.
            await systemEvents.AppendAsync(new SystemEvent
            {
                Type = !report.Passed ? SystemEventTypes.JobGateViolated
                    : report.Errors > 0 ? SystemEventTypes.JobCompletedWithErrors
                    : SystemEventTypes.JobCompleted,
                Severity = report.Passed && report.Errors == 0 ? SystemEventSeverity.Info : SystemEventSeverity.Warning,
                BranchKey = job.BranchKey,
                InstanceKey = job.InstanceKey,
                JobId = completed.Id,
                Message = !report.Passed
                    ? $"Porovnání {job.BranchKey}/{job.InstanceKey} porušilo bránu ({report.Differing} odlišných z {report.Total})."
                    : report.Errors > 0
                        ? $"Porovnání {job.BranchKey}/{job.InstanceKey} dokončeno s chybami ({report.Errors} chyb, {report.Differing} odlišných z {report.Total})."
                        : $"Porovnání {job.BranchKey}/{job.InstanceKey} dokončeno ({report.Differing} odlišných z {report.Total}).",
                Detail = report.GateViolations.Count > 0 ? string.Join("\n", report.GateViolations) : null,
                OccurredAt = report.CompletedAt,
            }, CancellationToken.None);

            logger.LogInformation("Job {JobId} finalized: {Total} files, {Diff} differing.",
                completed.Id, report.Total, report.Differing);

            return new BatchFinished(
                completed.Id, job.BranchKey, job.InstanceKey,
                report.Total, report.Identical, report.Differing, report.Errors,
                report.FilesWithContentErrors, report.Passed,
                report.GateViolations.ToArray(), report.CompletedAt,
                completed.SourceAutomationId);
        }
        catch (ConcurrencyConflictException)
        {
            logger.LogInformation("Job {JobId} already finalized.", job.Id);
            return null;
        }
    }
}
