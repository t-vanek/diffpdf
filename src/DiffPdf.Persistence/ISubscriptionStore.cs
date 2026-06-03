using DiffPdf.Core.Models;

namespace DiffPdf.Persistence;

/// <summary>
/// Persistence for notification subscriptions — runtime-managed delivery rules. Id-addressed
/// (no key uniqueness). Updates use optimistic concurrency (expectedVersion).
/// </summary>
public interface ISubscriptionStore
{
    Task<NotificationSubscription> CreateAsync(NotificationSubscription subscription, CancellationToken ct = default);

    Task<NotificationSubscription?> GetAsync(Guid id, CancellationToken ct = default);

    Task<IReadOnlyList<NotificationSubscription>> ListAsync(CancellationToken ct = default);

    /// <summary>All enabled subscriptions — read by the dispatcher when a batch finishes.</summary>
    Task<IReadOnlyList<NotificationSubscription>> ListEnabledAsync(CancellationToken ct = default);

    /// <summary>Updates a subscription (matched by Id); throws ConcurrencyConflictException on version mismatch.</summary>
    Task<NotificationSubscription> UpdateAsync(NotificationSubscription subscription, long expectedVersion, CancellationToken ct = default);

    Task<bool> DeleteAsync(Guid id, CancellationToken ct = default);
}
