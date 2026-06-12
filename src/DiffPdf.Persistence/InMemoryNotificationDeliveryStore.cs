using System.Collections.Concurrent;
using DiffPdf.Core.Models;

namespace DiffPdf.Persistence;

/// <summary>
/// Thread-safe in-memory notification outbox for single-instance / dev use. Mirrors the semantics of the
/// relational store. Does not survive restart.
/// </summary>
public sealed class InMemoryNotificationDeliveryStore : INotificationDeliveryStore
{
    private readonly ConcurrentDictionary<Guid, NotificationDelivery> _byId = new();

    public Task AddRangeAsync(IReadOnlyList<NotificationDelivery> deliveries, CancellationToken ct = default)
    {
        foreach (var d in deliveries)
            _byId[d.Id] = d;
        return Task.CompletedTask;
    }

    public Task<NotificationDelivery?> GetAsync(Guid id, CancellationToken ct = default) =>
        Task.FromResult(_byId.TryGetValue(id, out var d) ? d : null);

    public Task<IReadOnlyList<NotificationDelivery>> ListDueAsync(DateTimeOffset now, int limit, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<NotificationDelivery>>(_byId.Values
            .Where(d => d.Status is NotificationDeliveryStatus.Pending or NotificationDeliveryStatus.Failed
                        && d.NextAttemptAt is { } due && due <= now)
            .OrderBy(d => d.NextAttemptAt)
            .Take(limit)
            .ToList());

    public Task<IReadOnlyList<NotificationDelivery>> ListRecentAsync(int limit, NotificationDeliveryStatus? status = null, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<NotificationDelivery>>(_byId.Values
            .Where(d => status is null || d.Status == status)
            .OrderByDescending(d => d.CreatedAt)
            .Take(limit)
            .ToList());

    public Task MarkSentAsync(Guid id, DateTimeOffset sentAt, CancellationToken ct = default)
    {
        Mutate(id, d => d with
        {
            Status = NotificationDeliveryStatus.Sent,
            SentAt = sentAt,
            NextAttemptAt = null,
            LastError = null,
        });
        return Task.CompletedTask;
    }

    public Task MarkFailedAsync(Guid id, string error, int attemptCount, DateTimeOffset? nextAttemptAt, bool deadLetter, CancellationToken ct = default)
    {
        Mutate(id, d => d with
        {
            Status = deadLetter ? NotificationDeliveryStatus.DeadLetter : NotificationDeliveryStatus.Failed,
            AttemptCount = attemptCount,
            NextAttemptAt = deadLetter ? null : nextAttemptAt,
            LastError = error,
        });
        return Task.CompletedTask;
    }

    public Task<int> CountAsync(NotificationDeliveryStatus status, CancellationToken ct = default) =>
        Task.FromResult(_byId.Values.Count(d => d.Status == status));

    public Task<bool> RequeueAsync(Guid id, DateTimeOffset now, CancellationToken ct = default)
    {
        if (!_byId.ContainsKey(id))
            return Task.FromResult(false);
        Mutate(id, d => d with
        {
            Status = NotificationDeliveryStatus.Pending,
            AttemptCount = 0,
            NextAttemptAt = now,
            LastError = null,
        });
        return Task.FromResult(true);
    }

    public Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default)
    {
        int removed = 0;
        foreach (var d in _byId.Values.Where(d =>
                     d.Status is NotificationDeliveryStatus.Sent or NotificationDeliveryStatus.DeadLetter
                     && d.CreatedAt < cutoff).ToList())
            if (_byId.TryRemove(d.Id, out _))
                removed++;
        return Task.FromResult(removed);
    }

    private void Mutate(Guid id, Func<NotificationDelivery, NotificationDelivery> update)
    {
        while (_byId.TryGetValue(id, out var current))
            if (_byId.TryUpdate(id, update(current), current))
                return;
    }
}
