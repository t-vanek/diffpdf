using DiffPdf.Core.Abstractions;
using DiffPdf.Messaging.Messages;
using DiffPdf.Persistence;
using DiffPdf.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DiffPdf.Messaging.Handlers;

/// <summary>
/// Idempotent handler for <see cref="RunBatchComparison"/>. Claims the job with
/// optimistic concurrency, runs the comparison with version-checked progress,
/// and completes or fails it. Safe under duplicate delivery and multiple worker
/// instances: only one worker can transition Queued → Running.
/// </summary>
public sealed class RunBatchComparisonHandler
{
    public static async Task Handle(
        RunBatchComparison command,
        IJobStore jobStore,
        IBatchComparer batchComparer,
        IStorageProvisioner provisioner,
        IJobStoragePathProvider paths,
        IJobProgressPublisher progressPublisher,
        IWorkerInstanceIdProvider workerInstance,
        IOptions<WorkerOptions> workerOptions,
        ILogger<RunBatchComparisonHandler> logger,
        CancellationToken ct)
    {
        var job = await jobStore.TryStartAsync(command.JobId, workerInstance.WorkerInstanceId, workerOptions.Value.JobLease, ct);
        if (job is null)
        {
            logger.LogInformation("Job {JobId} not claimed (already running/finished or missing) — skipping.", command.JobId);
            return;
        }

        var tracker = new JobProgressTracker(jobStore, progressPublisher, job, logger);

        try
        {
            await provisioner.EnsureJobFoldersAsync(job, ct);
            await progressPublisher.PublishAsync(JobProgressChanged.From(job), ct);

            string artifactsPath = paths.GetArtifactsPath(job);
            var report = await batchComparer.CompareAsync(job.Request, artifactsPath, tracker, ct);

            await tracker.DrainAsync();
            var completed = await jobStore.CompleteAsync(tracker.Current.Id, report, tracker.Current.Version, ct);
            await progressPublisher.PublishAsync(JobProgressChanged.From(completed), ct);

            logger.LogInformation("Job {JobId} completed: {Total} files, {Diff} differing.",
                completed.Id, report.Total, report.Differing);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Job {JobId} failed", job.Id);
            try
            {
                await tracker.DrainAsync();
                var failed = await jobStore.FailAsync(tracker.Current.Id, ex.Message, tracker.Current.Version, CancellationToken.None);
                await progressPublisher.PublishAsync(JobProgressChanged.From(failed), CancellationToken.None);
            }
            catch (Exception failEx)
            {
                logger.LogError(failEx, "Could not mark job {JobId} as failed", job.Id);
            }
            throw; // surface to Wolverine; retries are idempotent (job is no longer Queued)
        }
    }
}
