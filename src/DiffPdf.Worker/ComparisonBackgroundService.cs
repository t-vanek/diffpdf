using DiffPdf.Core.Abstractions;
using DiffPdf.Core.Models;
using DiffPdf.Persistence;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DiffPdf.Worker;

/// <summary>Drains the job queue and runs each batch comparison, updating the store.</summary>
public sealed class ComparisonBackgroundService(
    IComparisonJobQueue queue,
    IJobStore jobStore,
    IBatchComparer batchComparer,
    IOptions<WorkerOptions> options,
    ILogger<ComparisonBackgroundService> logger) : BackgroundService
{
    private readonly WorkerOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Comparison worker started. Artifacts: {Root}", _options.ArtifactRoot);

        await foreach (var jobId in queue.DequeueAllAsync(stoppingToken))
        {
            try
            {
                await ProcessAsync(jobId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled error processing job {JobId}", jobId);
                await MarkFailedAsync(jobId, ex.Message, stoppingToken);
            }
        }
    }

    private async Task ProcessAsync(Guid jobId, CancellationToken ct)
    {
        var job = await jobStore.GetAsync(jobId, ct);
        if (job is null)
        {
            logger.LogWarning("Job {JobId} not found; skipping.", jobId);
            return;
        }

        job = job with { Status = JobStatus.Running, StartedAt = DateTimeOffset.UtcNow };
        await jobStore.UpdateAsync(job, ct);

        string artifactDir = Path.Combine(_options.ArtifactRoot, jobId.ToString("N"));
        var progress = new Progress<int>(async done =>
        {
            var current = await jobStore.GetAsync(jobId, ct);
            if (current is not null)
                await jobStore.UpdateAsync(current with { ProcessedCount = done }, ct);
        });

        var report = await batchComparer.CompareAsync(job.Request, artifactDir, progress, ct);

        var finalJob = await jobStore.GetAsync(jobId, ct) ?? job;
        await jobStore.UpdateAsync(finalJob with
        {
            Status = JobStatus.Completed,
            CompletedAt = DateTimeOffset.UtcNow,
            TotalCount = report.Total,
            ProcessedCount = report.Total,
            Report = report,
        }, ct);

        logger.LogInformation(
            "Job {JobId} completed: {Total} files, {Diff} differing.",
            jobId, report.Total, report.Differing);
    }

    private async Task MarkFailedAsync(Guid jobId, string error, CancellationToken ct)
    {
        var job = await jobStore.GetAsync(jobId, ct);
        if (job is not null)
            await jobStore.UpdateAsync(
                job with { Status = JobStatus.Failed, CompletedAt = DateTimeOffset.UtcNow, Error = error }, ct);
    }
}
