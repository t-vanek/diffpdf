using DiffPdf.Core.Abstractions;
using DiffPdf.Core.Models;
using DiffPdf.Messaging.Messages;
using DiffPdf.Messaging.Scheduling;
using DiffPdf.Persistence;
using DiffPdf.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wolverine;

namespace DiffPdf.Messaging.Handlers;

/// <summary>
/// Claims the job (idempotent, optimistic concurrency) and kicks off indexing. The single chokepoint that
/// enforces the per-branch sequential queue for <b>every</b> launch path: under the per-branch lock, a job
/// transitions Queued → Running only when the branch is not held and has no other Running/Paused job. When
/// it cannot start, the job stays Queued and the dispatcher re-kicks it once the branch frees / resumes.
/// </summary>
public sealed class RunBatchComparisonHandler
{
    public static async Task Handle(
        RunBatchComparison command,
        IJobStore jobStore,
        IBranchStore branches,
        BranchDispatchLocks locks,
        IJobProgressPublisher progressPublisher,
        IWorkerInstanceIdProvider workerInstance,
        IOptions<WorkerOptions> workerOptions,
        IMessageBus bus,
        ILogger<RunBatchComparisonHandler> logger,
        CancellationToken ct)
    {
        using var logScope = logger.BeginScope(new Dictionary<string, object> { ["JobId"] = command.JobId });

        var job = await jobStore.GetAsync(command.JobId, ct);
        if (job is null || job.Status != JobStatus.Queued)
        {
            logger.LogInformation("Job {JobId} not claimable (already running/finished or missing) — skipping.", command.JobId);
            return;
        }

        var gate = locks.For(job.BranchId);
        await gate.WaitAsync(ct);
        try
        {
            var branch = await branches.GetByIdAsync(job.BranchId, ct);
            if (branch is { QueuePaused: true })
            {
                logger.LogInformation("Job {JobId} held: branch {Branch} queue is paused.", job.Id, job.BranchKey);
                return; // stays Queued; the dispatcher re-kicks it on resume
            }

            var siblings = await jobStore.ListActiveAndDraftByBranchAsync(job.BranchId, ct);
            if (siblings.Any(j => j.Id != job.Id && j.Status is JobStatus.Running or JobStatus.Paused))
            {
                logger.LogInformation("Job {JobId} held: branch {Branch} already has an active job.", job.Id, job.BranchKey);
                return; // stays Queued; the dispatcher re-kicks it when the branch frees
            }

            var started = await jobStore.TryStartAsync(command.JobId, workerInstance.WorkerInstanceId, workerOptions.Value.JobLease, ct);
            if (started is null)
            {
                logger.LogInformation("Job {JobId} not claimed (already running/finished or missing) — skipping.", command.JobId);
                return;
            }

            await progressPublisher.PublishAsync(JobProgressChanged.From(started), ct);
            await bus.PublishAsync(new IndexBatch(started.Id));
        }
        finally
        {
            gate.Release();
        }
    }
}
