using DiffPdf.Core.Models;

namespace DiffPdf.Persistence;

/// <summary>
/// Persistence for control checks — runtime-managed automated control/monitoring definitions.
/// Updates use optimistic concurrency (expectedVersion) and surface
/// <see cref="DiffPdf.Core.Storage.DuplicateKeyException"/> /
/// <see cref="DiffPdf.Core.Storage.ConcurrencyConflictException"/>.
/// </summary>
public interface IControlCheckStore
{
    /// <summary>Creates a check; throws DuplicateKeyException if <see cref="ControlCheck.Key"/> already exists.</summary>
    Task<ControlCheck> CreateAsync(ControlCheck check, CancellationToken ct = default);

    Task<ControlCheck?> GetAsync(Guid id, CancellationToken ct = default);

    Task<ControlCheck?> GetByKeyAsync(string key, CancellationToken ct = default);

    Task<IReadOnlyList<ControlCheck>> ListAsync(CancellationToken ct = default);

    /// <summary>All enabled checks — the runner's per-tick hot path.</summary>
    Task<IReadOnlyList<ControlCheck>> ListEnabledAsync(CancellationToken ct = default);

    /// <summary>
    /// Updates a check (matched by <see cref="ControlCheck.Id"/>); throws ConcurrencyConflictException on
    /// version mismatch and DuplicateKeyException on key collision.
    /// </summary>
    Task<ControlCheck> UpdateAsync(ControlCheck check, long expectedVersion, CancellationToken ct = default);

    Task<bool> DeleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>Records the last-run timestamp and outcome (best-effort; no version guard).</summary>
    Task TouchLastRunAsync(Guid id, DateTimeOffset at, CheckRunOutcome outcome, CancellationToken ct = default);
}
