using DiffPdf.Core.Models;

namespace DiffPdf.Persistence;

/// <summary>Filter for listing triggers (deleted excluded unless requested).</summary>
public sealed record TriggerQuery
{
    public Guid? BranchId { get; init; }
    public Guid? InstanceId { get; init; }
    public TriggerStatus? Status { get; init; }
    public bool IncludeDeleted { get; init; }
}

/// <summary>
/// Persistence for triggers (managed launch entities). Soft delete only — a deleted trigger is kept so
/// its run history, jobs and results survive. Mutations use optimistic concurrency via <c>Version</c>.
/// </summary>
public interface ITriggerStore
{
    /// <summary>Creates a trigger; throws <see cref="DiffPdf.Core.Storage.DuplicateKeyException"/> if it would be a
    /// second non-deleted default for the instance.</summary>
    Task<Trigger> CreateAsync(Trigger trigger, CancellationToken ct = default);

    /// <summary>Gets a trigger by id, including soft-deleted ones (so detail/history stays reachable).</summary>
    Task<Trigger?> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>The instance's single non-deleted default trigger, or null. Used for idempotent provisioning.</summary>
    Task<Trigger?> GetDefaultForInstanceAsync(Guid instanceId, CancellationToken ct = default);

    Task<IReadOnlyList<Trigger>> ListAsync(TriggerQuery query, CancellationToken ct = default);

    Task<Trigger> UpdateAsync(Trigger trigger, long expectedVersion, CancellationToken ct = default);

    /// <summary>Enables/disables a trigger (sets Status Active/Disabled accordingly). Null if not found.</summary>
    Task<Trigger?> SetEnabledAsync(Guid id, bool enabled, string? actor, CancellationToken ct = default);

    /// <summary>Soft-deletes a trigger (IsDeleted=true, Status=Deleted). Returns the deleted trigger, or null if missing.</summary>
    Task<Trigger?> SoftDeleteAsync(Guid id, string? actor, CancellationToken ct = default);

    /// <summary>Records a run: bumps RunCount and sets LastRunAt/LastOutcome (best-effort, no version guard).</summary>
    Task TouchLastRunAsync(Guid id, DateTimeOffset at, string outcome, CancellationToken ct = default);
}
