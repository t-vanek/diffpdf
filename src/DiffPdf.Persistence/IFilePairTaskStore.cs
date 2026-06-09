using DiffPdf.Core.Models;

namespace DiffPdf.Persistence;

/// <summary>Persistence for per-file-pair tasks (phase 5).</summary>
public interface IFilePairTaskStore
{
    Task CreateManyAsync(IReadOnlyList<FilePairTask> tasks, CancellationToken ct = default);

    /// <summary>Atomically claims a Queued task (Queued → Running). Null if it could not be claimed.</summary>
    Task<FilePairTask?> TryClaimAsync(Guid taskId, string workerId, TimeSpan lease, CancellationToken ct = default);

    /// <summary>
    /// Records a claimed task's terminal result. Guarded on <c>Running</c>: returns <c>true</c> if it transitioned
    /// the task (the caller "won" and should advance the job's processed counter), or <c>false</c> if the task was
    /// already terminal — a duplicate/late completion (e.g. a lease-expiry re-run) that must NOT be double-counted.
    /// </summary>
    Task<bool> CompleteAsync(Guid taskId, FilePairResult result, FilePairTaskStatus status, CancellationToken ct = default);

    Task FailAsync(Guid taskId, string error, CancellationToken ct = default);

    /// <summary>Returns a claimed task to the queue (Running → Queued) for another attempt.</summary>
    Task RequeueAsync(Guid taskId, CancellationToken ct = default);

    /// <summary>Resets a task (any state → Queued, clears result/error/attempts) for a manual retry.</summary>
    Task RequeueForRetryAsync(Guid taskId, CancellationToken ct = default);

    /// <summary>
    /// Marks a job's still-<c>Queued</c> tasks as <c>Skipped</c> (terminal). Called when a job is cancelled so its
    /// un-started pairs do not linger as "pending" comparisons forever. Returns the number of tasks skipped.
    /// </summary>
    Task<int> SkipPendingForJobAsync(Guid jobId, CancellationToken ct = default);

    /// <summary>
    /// One-time cleanup: marks every still-<c>Queued</c> task whose owning job is already terminal
    /// (Cancelled/Failed/Completed) as <c>Skipped</c> — heals pairs stranded by a cancel that predated the
    /// cancel→skip fix. Returns rows skipped. (No-op for the in-memory store, which has no startup sweep.)
    /// </summary>
    Task<int> SkipPendingForTerminalJobsAsync(CancellationToken ct = default);

    /// <summary>
    /// Requeues tasks whose lease expired (crashed worker) and returns their
    /// (jobId, taskId) so they can be re-dispatched. Enables resume.
    /// </summary>
    Task<IReadOnlyList<(Guid JobId, Guid TaskId)>> RequeueStaleAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns <c>Running</c> tasks to the queue (Running → Queued, clears the lease, bumps Version) and yields
    /// their (jobId, taskId) for re-dispatch. <paramref name="lockedBy"/> non-null → only that worker's tasks
    /// (graceful shutdown: release THIS process's in-flight pairs, multi-replica-safe); null → ALL Running tasks
    /// (single-instance startup orphan reclaim after a hard crash). Mirrors <see cref="RequeueStaleAsync"/> but is
    /// NOT gated on lease expiry — these are live in-flight tasks being deliberately released.
    /// </summary>
    Task<IReadOnlyList<(Guid JobId, Guid TaskId)>> RequeueRunningTasksAsync(string? lockedBy, CancellationToken ct = default);

    /// <summary>
    /// <c>Queued</c> tasks under a still-<c>Running</c>, already-indexed (TotalCount &gt; 0) job whose last
    /// progress is older than <paramref name="idleSince"/> — pairs whose <c>CompareFilePair</c> dispatch was
    /// lost (so they have no lease for the stale-task sweeper to revive). Re-publishing their messages
    /// (idempotent — the claim guard dedups) unwedges a job that would otherwise never reach Processed &gt;= Total.
    /// (No-op for the in-memory store, which has no cross-store job view.)
    /// </summary>
    Task<IReadOnlyList<(Guid JobId, Guid TaskId)>> ListStaleQueuedAsync(DateTimeOffset idleSince, int limit, CancellationToken ct = default);

    Task<IReadOnlyList<FilePairTask>> ListByJobAsync(Guid jobId, CancellationToken ct = default);

    /// <summary>Counts active (Queued or Running) file-pair tasks across all jobs (operational backlog depth).</summary>
    Task<int> CountActiveAsync(CancellationToken ct = default);

    /// <summary>
    /// Counts file-pair tasks grouped by status across the given jobs (the per-scope comparison breakdown for
    /// the branch/instance detail views). Empty <paramref name="jobIds"/> yields an empty result.
    /// </summary>
    Task<IReadOnlyDictionary<FilePairTaskStatus, int>> CountByStatusForJobsAsync(
        IReadOnlyCollection<Guid> jobIds, CancellationToken ct = default);

    /// <summary>Bulk-deletes all file-pair tasks belonging to the given jobs (DB-row retention). Returns rows removed.</summary>
    Task<int> DeleteForJobsAsync(IReadOnlyCollection<Guid> jobIds, CancellationToken ct = default);
}
