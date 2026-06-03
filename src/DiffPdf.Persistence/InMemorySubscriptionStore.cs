using System.Collections.Concurrent;
using DiffPdf.Core.Models;
using DiffPdf.Core.Storage;

namespace DiffPdf.Persistence;

/// <summary>
/// Thread-safe in-memory subscription store for single-instance / dev use. Mirrors the
/// optimistic-concurrency semantics of the relational stores. Does not survive restart.
/// </summary>
public sealed class InMemorySubscriptionStore : ISubscriptionStore
{
    private readonly ConcurrentDictionary<Guid, NotificationSubscription> _byId = new();
    private readonly object _gate = new();

    public Task<NotificationSubscription> CreateAsync(NotificationSubscription subscription, CancellationToken ct = default)
    {
        var created = subscription with { Version = 1 };
        _byId[created.Id] = created;
        return Task.FromResult(created);
    }

    public Task<NotificationSubscription?> GetAsync(Guid id, CancellationToken ct = default) =>
        Task.FromResult(_byId.TryGetValue(id, out var s) ? s : null);

    public Task<IReadOnlyList<NotificationSubscription>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<NotificationSubscription>>(_byId.Values.OrderBy(s => s.CreatedAt).ToList());

    public Task<IReadOnlyList<NotificationSubscription>> ListEnabledAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<NotificationSubscription>>(
            _byId.Values.Where(s => s.Enabled).OrderBy(s => s.CreatedAt).ToList());

    public Task<NotificationSubscription> UpdateAsync(NotificationSubscription subscription, long expectedVersion, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_byId.TryGetValue(subscription.Id, out var existing))
                throw new ConcurrencyConflictException($"Subscription {subscription.Id} not found.");
            if (existing.Version != expectedVersion)
                throw new ConcurrencyConflictException($"Subscription {subscription.Id} version {existing.Version} != expected {expectedVersion}.");

            var updated = subscription with { UpdatedAt = DateTimeOffset.UtcNow, Version = existing.Version + 1 };
            _byId[updated.Id] = updated;
            return Task.FromResult(updated);
        }
    }

    public Task<bool> DeleteAsync(Guid id, CancellationToken ct = default) =>
        Task.FromResult(_byId.TryRemove(id, out _));
}
