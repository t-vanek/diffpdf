using DiffPdf.Core.Abstractions;
using DiffPdf.Core.Models;
using DiffPdf.Messaging.Messages;
using DiffPdf.Persistence;
using Wolverine;

namespace DiffPdf.Messaging.Scheduling;

/// <summary>
/// Resumes a paused job and re-dispatches its still-pending file pairs (or finalizes if none remain).
/// Shared by the job resume endpoint and the branch-queue resume action so the two never drift.
/// </summary>
public interface IJobResumeService
{
    /// <summary>Returns the resumed job (null if it was not Paused) and how many pending pairs were re-dispatched.</summary>
    Task<(ComparisonJob? Job, int Redispatched)> ResumeAsync(Guid jobId, CancellationToken ct = default);
}

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
        var pending = tasks.Where(t => t.Status == FilePairTaskStatus.Queued).ToList();
        foreach (var t in pending)
            await bus.PublishAsync(new CompareFilePair(jobId, t.Id));
        if (pending.Count == 0)
            await bus.PublishAsync(new FinalizeBatch(jobId)); // everything finished while paused — finalize directly

        await progress.PublishAsync(JobProgressChanged.From(resumed), ct);
        return (resumed, pending.Count);
    }
}
