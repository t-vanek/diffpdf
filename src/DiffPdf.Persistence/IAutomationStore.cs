using DiffPdf.Core.Models;

namespace DiffPdf.Persistence;

/// <summary>
/// Persistence for automations — runtime-managed trigger + step-pipeline definitions executed by the
/// automation engine. Updates use optimistic concurrency (expectedVersion) and surface
/// <see cref="DiffPdf.Core.Storage.DuplicateKeyException"/> /
/// <see cref="DiffPdf.Core.Storage.ConcurrencyConflictException"/>. The engine-state operations
/// (<see cref="TryBeginRunAsync"/> / <see cref="CompleteRunAsync"/> / <see cref="SetNextRunIfNullAsync"/> /
/// <see cref="TryClaimEventTriggerAsync"/>) intentionally bypass the version guard and touch a column set
/// disjoint from <see cref="UpdateAsync"/>, so user edits and engine state never clobber each other.
/// </summary>
public interface IAutomationStore
{
    /// <summary>Creates an automation; throws DuplicateKeyException if <see cref="Automation.Key"/> already exists.</summary>
    Task<Automation> CreateAsync(Automation automation, CancellationToken ct = default);

    Task<Automation?> GetAsync(Guid id, CancellationToken ct = default);

    Task<Automation?> GetByKeyAsync(string key, CancellationToken ct = default);

    Task<IReadOnlyList<Automation>> ListAsync(CancellationToken ct = default);

    /// <summary>All enabled automations — the engine's per-tick hot path.</summary>
    Task<IReadOnlyList<Automation>> ListEnabledAsync(CancellationToken ct = default);

    /// <summary>
    /// Updates an automation (matched by <see cref="Automation.Id"/>); throws ConcurrencyConflictException
    /// on version mismatch and DuplicateKeyException on key collision.
    /// </summary>
    Task<Automation> UpdateAsync(Automation automation, long expectedVersion, CancellationToken ct = default);

    /// <summary>Quick mute toggle for the automation's notifications. Does not bump the Version (it must not
    /// conflict with an open editor); returns the updated automation, or null when unknown.</summary>
    Task<Automation?> SetNotificationsEnabledAsync(Guid id, bool enabled, CancellationToken ct = default);

    Task<bool> DeleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Atomically claims a run: sets <see cref="Automation.RunningSince"/> to <paramref name="now"/> and —
    /// when <paramref name="advanceNextRunAtTo"/> is given (schedule fires) — advances
    /// <see cref="Automation.NextRunAt"/>, guarded by "no run in flight" (RunningSince is null or older
    /// than <paramref name="staleAfter"/>, so a crashed run never wedges the automation). Returns false
    /// when a run is in flight or the automation no longer exists.
    /// </summary>
    Task<bool> TryBeginRunAsync(Guid id, DateTimeOffset now, DateTimeOffset? advanceNextRunAtTo, TimeSpan staleAfter, CancellationToken ct = default);

    /// <summary>Completes a run: clears RunningSince and records LastRunAt / LastOutcome /
    /// ConsecutiveFailures (best-effort; no version guard).</summary>
    Task CompleteRunAsync(Guid id, DateTimeOffset at, AutomationRunOutcome outcome, int consecutiveFailures, CancellationToken ct = default);

    /// <summary>Seeds <see cref="Automation.NextRunAt"/> for a schedule automation that has none yet
    /// (conditional — a no-op when it is already set).</summary>
    Task SetNextRunIfNullAsync(Guid id, DateTimeOffset next, CancellationToken ct = default);

    /// <summary>
    /// Atomically claims an event trigger: sets <see cref="Automation.LastEventTriggeredAt"/> to
    /// <paramref name="now"/> when the automation is enabled and the previous claim is older than
    /// <paramref name="debounce"/>. Returns false when debounced, disabled or missing.
    /// </summary>
    Task<bool> TryClaimEventTriggerAsync(Guid id, DateTimeOffset now, TimeSpan debounce, CancellationToken ct = default);
}
