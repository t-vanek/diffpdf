using DiffPdf.Core.Models;

namespace DiffPdf.Persistence;

/// <summary>
/// Append-only history of trigger runs. <see cref="TriggerRun.TriggerId"/> is not a foreign key, so run
/// history outlives a soft-deleted trigger.
/// </summary>
public interface ITriggerRunStore
{
    Task AddAsync(TriggerRun run, CancellationToken ct = default);

    /// <summary>A trigger's runs, newest first.</summary>
    Task<IReadOnlyList<TriggerRun>> ListByTriggerAsync(Guid triggerId, int limit = 50, CancellationToken ct = default);

    /// <summary>An earlier run with the same idempotency key for this trigger (for dedupe), or null.</summary>
    Task<TriggerRun?> GetByIdempotencyKeyAsync(Guid triggerId, string idempotencyKey, CancellationToken ct = default);

    /// <summary>Bulk-deletes trigger runs started before the cutoff (DB-row retention). Returns rows removed.</summary>
    Task<int> DeleteStartedBeforeAsync(DateTimeOffset startedBefore, CancellationToken ct = default);
}
