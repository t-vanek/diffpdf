using DiffPdf.Core.Abstractions;
using DiffPdf.Messaging.Messages;
using DiffPdf.Persistence;
using DiffPdf.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wolverine;

namespace DiffPdf.Messaging.Handlers;

/// <summary>
/// Claims the job (idempotent, optimistic concurrency) and kicks off indexing.
/// Only one worker can transition Queued → Running; duplicate deliveries skip.
/// </summary>
public sealed class RunBatchComparisonHandler
{
    public static async Task Handle(
        RunBatchComparison command,
        IJobStore jobStore,
        IJobProgressPublisher progressPublisher,
        IWorkerInstanceIdProvider workerInstance,
        IOptions<WorkerOptions> workerOptions,
        IMessageBus bus,
        ILogger<RunBatchComparisonHandler> logger,
        CancellationToken ct)
    {
        var job = await jobStore.TryStartAsync(command.JobId, workerInstance.WorkerInstanceId, workerOptions.Value.JobLease, ct);
        if (job is null)
        {
            logger.LogInformation("Job {JobId} not claimed (already running/finished or missing) — skipping.", command.JobId);
            return;
        }

        await progressPublisher.PublishAsync(JobProgressChanged.From(job), ct);
        await bus.PublishAsync(new IndexBatch(job.Id));
    }
}
