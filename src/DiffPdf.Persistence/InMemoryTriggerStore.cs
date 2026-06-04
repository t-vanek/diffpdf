using System.Collections.Concurrent;
using DiffPdf.Core.Models;
using DiffPdf.Core.Storage;

namespace DiffPdf.Persistence;

/// <summary>Thread-safe in-memory trigger store for dev/test. Mirrors the relational soft-delete +
/// optimistic-concurrency + single-default-per-instance semantics.</summary>
public sealed class InMemoryTriggerStore : ITriggerStore
{
    private readonly ConcurrentDictionary<Guid, Trigger> _byId = new();
    private readonly object _gate = new();

    public Task<Trigger> CreateAsync(Trigger trigger, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (trigger.IsDefault && HasDefault(trigger.InstanceId, exceptId: null))
                throw new DuplicateKeyException($"A default trigger already exists for instance {trigger.InstanceId}.");
            var created = trigger with { Version = 1 };
            _byId[created.Id] = created;
            return Task.FromResult(created);
        }
    }

    public Task<Trigger?> GetAsync(Guid id, CancellationToken ct = default) =>
        Task.FromResult(_byId.TryGetValue(id, out var t) ? t : null);

    public Task<Trigger?> GetDefaultForInstanceAsync(Guid instanceId, CancellationToken ct = default) =>
        Task.FromResult(_byId.Values.FirstOrDefault(t => t.InstanceId == instanceId && t.IsDefault && !t.IsDeleted));

    public Task<IReadOnlyList<Trigger>> ListAsync(TriggerQuery query, CancellationToken ct = default)
    {
        IEnumerable<Trigger> q = _byId.Values;
        if (!query.IncludeDeleted) q = q.Where(t => !t.IsDeleted);
        if (query.BranchId is { } b) q = q.Where(t => t.BranchId == b);
        if (query.InstanceId is { } i) q = q.Where(t => t.InstanceId == i);
        if (query.Status is { } s) q = q.Where(t => t.Status == s);
        return Task.FromResult<IReadOnlyList<Trigger>>(q.OrderByDescending(t => t.CreatedAt).ToList());
    }

    public Task<Trigger> UpdateAsync(Trigger trigger, long expectedVersion, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_byId.TryGetValue(trigger.Id, out var existing))
                throw new ConcurrencyConflictException($"Trigger {trigger.Id} not found.");
            if (existing.Version != expectedVersion)
                throw new ConcurrencyConflictException($"Trigger {trigger.Id} version {existing.Version} != expected {expectedVersion}.");
            if (trigger.IsDefault && HasDefault(trigger.InstanceId, exceptId: trigger.Id))
                throw new DuplicateKeyException($"A default trigger already exists for instance {trigger.InstanceId}.");

            var updated = trigger with { UpdatedAt = DateTimeOffset.UtcNow, Version = existing.Version + 1 };
            _byId[updated.Id] = updated;
            return Task.FromResult(updated);
        }
    }

    public Task<Trigger?> SetEnabledAsync(Guid id, bool enabled, string? actor, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_byId.TryGetValue(id, out var t) || t.IsDeleted) return Task.FromResult<Trigger?>(null);
            var updated = t with
            {
                Enabled = enabled,
                Status = enabled ? TriggerStatus.Active : TriggerStatus.Disabled,
                UpdatedAt = DateTimeOffset.UtcNow,
                UpdatedBy = actor ?? t.UpdatedBy,
                Version = t.Version + 1,
            };
            _byId[id] = updated;
            return Task.FromResult<Trigger?>(updated);
        }
    }

    public Task<Trigger?> SoftDeleteAsync(Guid id, string? actor, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_byId.TryGetValue(id, out var t)) return Task.FromResult<Trigger?>(null);
            if (t.IsDeleted) return Task.FromResult<Trigger?>(t);
            var deleted = t with
            {
                IsDeleted = true,
                Status = TriggerStatus.Deleted,
                Enabled = false,
                DeletedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                UpdatedBy = actor ?? t.UpdatedBy,
                Version = t.Version + 1,
            };
            _byId[id] = deleted;
            return Task.FromResult<Trigger?>(deleted);
        }
    }

    public Task TouchLastRunAsync(Guid id, DateTimeOffset at, string outcome, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_byId.TryGetValue(id, out var t))
                _byId[id] = t with { LastRunAt = at, LastOutcome = outcome, RunCount = t.RunCount + 1 };
        }
        return Task.CompletedTask;
    }

    private bool HasDefault(Guid instanceId, Guid? exceptId) =>
        _byId.Values.Any(t => t.InstanceId == instanceId && t.IsDefault && !t.IsDeleted && t.Id != exceptId);
}
