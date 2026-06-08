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
        var pending = tasks.Where(t => t.Status == FilePairTaskStatus.Queued).ToList();
        foreach (var t in pending)
            await bus.PublishAsync(new CompareFilePair(jobId, t.Id));
        if (pending.Count == 0)
            await bus.PublishAsync(new FinalizeBatch(jobId)); // everything finished while paused — finalize directly

        await progress.PublishAsync(JobProgressChanged.From(resumed), ct);
        return (resumed, pending.Count);
    }
}
