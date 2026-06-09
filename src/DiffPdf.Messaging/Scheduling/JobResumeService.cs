using DiffPdf.Application.Abstractions;
using DiffPdf.Core.Abstractions;
using DiffPdf.Core.Models;
using DiffPdf.Messaging.Messages;
using DiffPdf.Persistence;
using Wolverine;

namespace DiffPdf.Messaging.Scheduling;

/// <inheritdoc />
public sealed class JobResumeService(
    IJobStore jobStore,
    IFilePairTaskStore taskStore,
    IJobProgressPublisher progress,
    IMessageBus bus) : IJobResumeService
{
    public async Task<(ComparisonJob? Job, int Redispatched)> ResumeAsync(Guid jobId, CancellationToken ct = default)
    {
        var resumed = await jobStore.ResumeAsync(jobId, ct);
        if (resumed is null)
            return (null, 0);

        var tasks = await taskStore.ListByJobAsync(jobId, ct);
        int pending = tasks.Count(t => t.Status == FilePairTaskStatus.Queued);

        // One durable, idempotent message rather than N un-tracked CompareFilePair publishes (+ a separate
        // empty-pending FinalizeBatch). The job is now Running, so IndexBatchHandler takes its already-indexed
        // path: it re-dispatches every Queued pair (the claim guard dedups) and nudges FinalizeBatch when all
        // pairs are terminal. Shrinks the mid-resume crash window from N publishes to 1 and self-heals on
        // redelivery; the stale-Queued sweeper remains the backstop for the instant before the publish lands.
        await bus.PublishAsync(new IndexBatch(jobId));

        await progress.PublishAsync(JobProgressChanged.From(resumed), ct);
        return (resumed, pending);
    }
}
