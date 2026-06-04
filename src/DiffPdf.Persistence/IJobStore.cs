using DiffPdf.Core.Models;

namespace DiffPdf.Persistence;

/// <summary>
/// Persistence for batch comparison jobs — the source of truth for job state.
/// State-changing methods use optimistic concurrency (expectedVersion) and
/// return the updated job (with the new version) so callers stay in sync.
/// </summary>
public interface IJobStore
{
    Task<ComparisonJob> CreateAsync(ComparisonJob job, CancellationToken ct = default);

    Task<ComparisonJob?> GetAsync(Guid id, CancellationToken ct = default);

    Task<IReadOnlyList<ComparisonJob>> ListAsync(JobListQuery query, CancellationToken ct = default);

    /// <summary>Total jobs matching the query's filter (scope/status), ignoring its paging window — for pagination metadata.</summary>
    Task<int> CountAsync(JobListQuery query, CancellationToken ct = default);

    /// <summary>Atomically claims a Queued job for a worker (Queued → Running). Null if it could not be claimed.</summary>
    Task<ComparisonJob?> TryStartAsync(Guid id, string workerId, TimeSpan lease, CancellationToken ct = default);

    /// <summary>Count of a branch's "active" jobs (Queued/Running/Paused) — the per-branch sequential queue's busy check.</summary>
    Task<int> CountActiveByBranchAsync(Guid branchId, CancellationToken ct = default);

    /// <summary>The next pending (Draft) job to release for a branch, ordered by Priority DESC then CreatedAt ASC. Null if none.</summary>
    Task<ComparisonJob?> NextDraftForBranchAsync(Guid branchId, CancellationToken ct = default);

    /// <summary>A branch's pending + active jobs (Draft/Queued/Running/Paused), for the queue-state read and branch "stop".</summary>
    Task<IReadOnlyList<ComparisonJob>> ListActiveAndDraftByBranchAsync(Guid branchId, CancellationToken ct = default);

    /// <summary>
    /// Running jobs that never finished indexing (TotalCount 0) and whose worker lease expired — stuck
    /// (the worker died during/before indexing, so there are no file-pair tasks for task recovery to revive).
    /// Recovering these frees the per-branch queue.
    /// </summary>
    Task<IReadOnlyList<ComparisonJob>> ListStaleUnindexedRunningAsync(DateTimeOffset leaseExpiredBefore, int limit, CancellationToken ct = default);

    /// <summary>Updates progress on a Running job; throws ConcurrencyConflictException on version/state mismatch.</summary>
    Task<ComparisonJob> UpdateProgressAsync(Guid id, int processedCount, int totalCount, long expectedVersion, CancellationToken ct = default);

    Task<ComparisonJob> CompleteAsync(Guid id, BatchComparisonReport report, long expectedVersion, CancellationToken ct = default);

    Task<ComparisonJob> FailAsync(Guid id, string error, long expectedVersion, CancellationToken ct = default);

    /// <summary>Cancels a Draft/Queued/Running job. Returns the cancelled job, or null if it could not be cancelled.</summary>
    Task<ComparisonJob?> CancelAsync(Guid id, CancellationToken ct = default);

    /// <summary>Transitions a Draft job to Queued (ready for the worker). Null if it was not in Draft.</summary>
    Task<ComparisonJob?> EnqueueAsync(Guid id, CancellationToken ct = default);

    /// <summary>Pauses a Running job (Running → Paused). Null if it was not Running.</summary>
    Task<ComparisonJob?> PauseAsync(Guid id, CancellationToken ct = default);

    /// <summary>Resumes a Paused job (Paused → Running). Null if it was not Paused.</summary>
    Task<ComparisonJob?> ResumeAsync(Guid id, CancellationToken ct = default);

    /// <summary>Reopens a finished (Completed/Failed) job for a retry, resetting its progress.</summary>
    Task<ComparisonJob?> ReopenAsync(Guid id, int processedCount, CancellationToken ct = default);

    /// <summary>Sets the total file-pair count once indexing is done.</summary>
    Task SetTotalAsync(Guid id, int total, CancellationToken ct = default);

    /// <summary>Atomically increments processed count and returns the new (processed, total).</summary>
    Task<(int Processed, int Total)> IncrementProcessedAsync(Guid id, CancellationToken ct = default);

    /// <summary>Finished jobs completed before the cutoff whose artifacts have not yet been pruned (retention).</summary>
    Task<IReadOnlyList<ComparisonJob>> ListPrunableArtifactsAsync(DateTimeOffset completedBefore, int limit, CancellationToken ct = default);

    /// <summary>Marks a job's on-disk artifacts as pruned, so retention skips it on the next pass.</summary>
    Task MarkArtifactsPrunedAsync(Guid id, DateTimeOffset at, CancellationToken ct = default);

    /// <summary>
    /// IDs of finished jobs (Completed/Failed/Cancelled) completed before the cutoff <b>whose artifacts have
    /// already been pruned</b> — the DB-row retention prune set. Requiring artifacts-pruned guarantees row
    /// deletion can never orphan on-disk reports. Bounded by <paramref name="limit"/>.
    /// </summary>
    Task<IReadOnlyList<Guid>> ListPrunableRowsAsync(DateTimeOffset completedBefore, int limit, CancellationToken ct = default);

    /// <summary>Bulk-deletes the given jobs. Returns the number of rows removed. (Delete their file-pair tasks first.)</summary>
    Task<int> DeleteByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default);

    /// <summary>Counts jobs grouped by status (for the operational backlog view). Doubles as a cheap DB ping.</summary>
    Task<IReadOnlyDictionary<JobStatus, int>> CountByStatusAsync(CancellationToken ct = default);
}
