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

    /// <summary>Atomically claims a Queued job for a worker (Queued → Running). Null if it could not be claimed.</summary>
    Task<ComparisonJob?> TryStartAsync(Guid id, string workerId, TimeSpan lease, CancellationToken ct = default);

    /// <summary>Updates progress on a Running job; throws ConcurrencyConflictException on version/state mismatch.</summary>
    Task<ComparisonJob> UpdateProgressAsync(Guid id, int processedCount, int totalCount, long expectedVersion, CancellationToken ct = default);

    Task<ComparisonJob> CompleteAsync(Guid id, BatchComparisonReport report, long expectedVersion, CancellationToken ct = default);

    Task<ComparisonJob> FailAsync(Guid id, string error, long expectedVersion, CancellationToken ct = default);

    /// <summary>Replaces the request of a not-yet-started (Queued) job. Null if it is not Queued / not found.</summary>
    Task<ComparisonJob?> UpdateRequestAsync(Guid id, BatchComparisonRequest request, CancellationToken ct = default);

    /// <summary>Deletes a finished (Completed/Failed/Cancelled) job. False if it is active / not found.</summary>
    Task<bool> DeleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>Cancels a Queued/Running/Paused job. Returns the cancelled job, or null if it could not be cancelled.</summary>
    Task<ComparisonJob?> CancelAsync(Guid id, CancellationToken ct = default);

    /// <summary>Pauses a Running job (Running → Paused), stopping further dispatch. Null if not Running.</summary>
    Task<ComparisonJob?> PauseAsync(Guid id, CancellationToken ct = default);

    /// <summary>Resumes a Paused job (Paused → Running). Null if not Paused; the caller re-dispatches pending pairs.</summary>
    Task<ComparisonJob?> ResumeAsync(Guid id, CancellationToken ct = default);

    /// <summary>Reopens a finished (Completed/Failed) job for a retry, resetting its progress.</summary>
    Task<ComparisonJob?> ReopenAsync(Guid id, int processedCount, CancellationToken ct = default);

    /// <summary>Sets the total file-pair count once indexing is done.</summary>
    Task SetTotalAsync(Guid id, int total, CancellationToken ct = default);

    /// <summary>Atomically increments processed count and returns the new (processed, total).</summary>
    Task<(int Processed, int Total)> IncrementProcessedAsync(Guid id, CancellationToken ct = default);
}
