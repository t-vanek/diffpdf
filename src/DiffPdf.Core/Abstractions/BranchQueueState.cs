namespace DiffPdf.Core.Abstractions;

/// <summary>A queue control action requested for an instance or a whole branch.</summary>
public enum QueueAction
{
    /// <summary>Add a run to the back of the branch queue (priority 0).</summary>
    Enqueue,
    /// <summary>Run now / as soon as the branch is free (priority 100, jumps ahead of plain enqueues).</summary>
    Run,
    /// <summary>Pause the running run (and, at branch level, hold the queue).</summary>
    Pause,
    /// <summary>Resume the paused run (and, at branch level, release the hold).</summary>
    Resume,
    /// <summary>Cancel the run (and, at branch level, also clear the rest of the queue).</summary>
    Stop,
}

/// <summary>
/// An instance's position in its branch's sequential queue, derived from its current job. <see cref="Idle"/>
/// (no active/pending job) is the default for any instance not listed in a <see cref="BranchQueueState"/>.
/// </summary>
public enum InstanceQueueStatus
{
    /// <summary>No pending or active job — can be run / enqueued.</summary>
    Idle,
    /// <summary>A Draft job is waiting in the branch queue (not yet released).</summary>
    Pending,
    /// <summary>Released to the worker (about to run).</summary>
    Queued,
    /// <summary>Actively running.</summary>
    Running,
    /// <summary>Paused; can be resumed or stopped.</summary>
    Paused,
}

/// <summary>One instance's queue state within a branch.</summary>
public sealed record InstanceQueueState(
    string BranchKey,
    string InstanceKey,
    InstanceQueueStatus Status,
    Guid? ActiveJobId,
    int Priority);

/// <summary>
/// A branch's queue snapshot: the hold flag, the per-status counts and the per-instance states (only
/// instances with a pending/active job are listed; others are <see cref="InstanceQueueStatus.Idle"/>).
/// Also the SignalR payload pushed on every queue change.
/// </summary>
public sealed record BranchQueueState(
    string BranchKey,
    bool QueuePaused,
    int Pending,
    int Queued,
    int Running,
    int Paused,
    IReadOnlyList<InstanceQueueState> Instances);

/// <summary>Result of a queue action: the updated branch state plus an optional human-readable note
/// (e.g. "nothing to compare", "already queued") to surface in the UI.</summary>
public sealed record QueueActionOutcome(BranchQueueState State, string? Message);

/// <summary>Pushes per-branch queue state to interested clients (SignalR). A no-op is acceptable.</summary>
public interface IBranchQueueStatePublisher
{
    Task PublishAsync(BranchQueueState state, CancellationToken ct = default);
}

/// <summary>Default no-op publisher used until a realtime transport (SignalR) is wired in.</summary>
public sealed class NullBranchQueueStatePublisher : IBranchQueueStatePublisher
{
    public Task PublishAsync(BranchQueueState state, CancellationToken ct = default) => Task.CompletedTask;
}
