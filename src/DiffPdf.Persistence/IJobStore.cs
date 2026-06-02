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
}
