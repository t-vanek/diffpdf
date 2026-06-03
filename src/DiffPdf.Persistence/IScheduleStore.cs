using DiffPdf.Core.Models;

namespace DiffPdf.Persistence;

/// <summary>
/// Persistence for comparison schedules — runtime-managed recurring batch definitions.
/// Updates use optimistic concurrency (expectedVersion) and surface
/// <see cref="DiffPdf.Core.Storage.DuplicateKeyException"/> /
/// <see cref="DiffPdf.Core.Storage.ConcurrencyConflictException"/>.
/// </summary>
public interface IScheduleStore
{
    /// <summary>Creates a schedule; throws DuplicateKeyException if (instanceId, key) already exists.</summary>
    Task<ComparisonSchedule> CreateAsync(ComparisonSchedule schedule, CancellationToken ct = default);

    Task<ComparisonSchedule?> GetAsync(Guid id, CancellationToken ct = default);

    Task<ComparisonSchedule?> GetByKeyAsync(Guid instanceId, string key, CancellationToken ct = default);

    Task<IReadOnlyList<ComparisonSchedule>> ListByInstanceAsync(Guid instanceId, CancellationToken ct = default);

    /// <summary>All enabled schedules across every scope — the scheduler's per-tick hot path.</summary>
    Task<IReadOnlyList<ComparisonSchedule>> ListEnabledAsync(CancellationToken ct = default);

    /// <summary>
    /// Updates a schedule (matched by <see cref="ComparisonSchedule.Id"/>); throws
    /// ConcurrencyConflictException on version mismatch and DuplicateKeyException on key collision.
    /// </summary>
    Task<ComparisonSchedule> UpdateAsync(ComparisonSchedule schedule, long expectedVersion, CancellationToken ct = default);

    Task<bool> DeleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>Records the last-run timestamp (best-effort; no version guard).</summary>
    Task TouchLastRunAsync(Guid id, DateTimeOffset at, CancellationToken ct = default);
}
