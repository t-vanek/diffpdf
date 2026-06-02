using DiffPdf.Core.Abstractions;
using DiffPdf.Core.Models;
using DiffPdf.Core.Storage;
using DiffPdf.Messaging.Messages;
using DiffPdf.Persistence;
using Microsoft.Extensions.Logging;

namespace DiffPdf.Messaging.Handlers;

/// <summary>Aggregates file-pair results into the batch report and completes the job (idempotent).</summary>
public sealed class FinalizeBatchHandler
{
    public static async Task Handle(
        FinalizeBatch command,
        IJobStore jobStore,
        IFilePairTaskStore taskStore,
        IJobProgressPublisher progressPublisher,
        ILogger<FinalizeBatchHandler> logger,
        CancellationToken ct)
    {
        var job = await jobStore.GetAsync(command.JobId, ct);
        if (job is null || job.Status != JobStatus.Running)
            return; // already finalized or not ready

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
            var completed = await jobStore.CompleteAsync(job.Id, report, job.Version, ct);
            await progressPublisher.PublishAsync(JobProgressChanged.From(completed), ct);
            logger.LogInformation("Job {JobId} finalized: {Total} files, {Diff} differing.",
                completed.Id, report.Total, report.Differing);
        }
        catch (ConcurrencyConflictException)
        {
            logger.LogInformation("Job {JobId} already finalized.", job.Id);
        }
    }
}
