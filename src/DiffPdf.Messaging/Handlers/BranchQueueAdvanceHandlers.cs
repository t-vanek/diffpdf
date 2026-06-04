using DiffPdf.Messaging.Messages;
using DiffPdf.Messaging.Scheduling;
using DiffPdf.Persistence;

namespace DiffPdf.Messaging.Handlers;

/// <summary>
/// Advances the per-branch sequential queue when a job reaches a terminal state. Runs alongside the
/// notification handlers (Wolverine allows multiple handlers per message): once a batch finishes or fails,
/// the branch frees up and the dispatcher releases the next pending job. Cancellation is advanced from the
/// API (it has no domain event).
/// </summary>
public sealed class BranchQueueAdvanceHandlers
{
    public static async Task Handle(BatchFinished evt, IBranchStore branches, IBranchQueueDispatcher dispatcher, CancellationToken ct)
    {
        if (await branches.GetByKeyAsync(evt.BranchKey, ct) is { } branch)
            await dispatcher.DispatchBranchAsync(branch.Id, ct);
    }

    public static async Task Handle(BatchFailed evt, IBranchStore branches, IBranchQueueDispatcher dispatcher, CancellationToken ct)
    {
        if (await branches.GetByKeyAsync(evt.BranchKey, ct) is { } branch)
            await dispatcher.DispatchBranchAsync(branch.Id, ct);
    }
}
