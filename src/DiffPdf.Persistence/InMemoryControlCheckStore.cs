using System.Collections.Concurrent;
using DiffPdf.Core.Models;
using DiffPdf.Core.Storage;

namespace DiffPdf.Persistence;

/// <summary>
/// Thread-safe in-memory control-check store for single-instance / dev use. Mirrors the
/// duplicate-key + optimistic-concurrency semantics of the relational stores. Does not
/// survive restart.
/// </summary>
public sealed class InMemoryControlCheckStore : IControlCheckStore
{
    private readonly ConcurrentDictionary<Guid, ControlCheck> _byId = new();
    private readonly object _gate = new();

    public Task<ControlCheck> CreateAsync(ControlCheck check, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_byId.Values.Any(c => c.Key == check.Key))
                throw new DuplicateKeyException($"Control check '{check.Key}' already exists.");

            var created = check with { Version = 1 };
            _byId[created.Id] = created;
            return Task.FromResult(created);
        }
    }

    public Task<ControlCheck?> GetAsync(Guid id, CancellationToken ct = default) =>
        Task.FromResult(_byId.TryGetValue(id, out var c) ? c : null);

    public Task<ControlCheck?> GetByKeyAsync(string key, CancellationToken ct = default) =>
        Task.FromResult(_byId.Values.FirstOrDefault(c => c.Key == key));

    public Task<IReadOnlyList<ControlCheck>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ControlCheck>>(_byId.Values.OrderBy(c => c.Key).ToList());

    public Task<IReadOnlyList<ControlCheck>> ListEnabledAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ControlCheck>>(
            _byId.Values.Where(c => c.Enabled).OrderBy(c => c.Key).ToList());

    public Task<ControlCheck> UpdateAsync(ControlCheck check, long expectedVersion, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_byId.TryGetValue(check.Id, out var existing))
                throw new ConcurrencyConflictException($"Control check {check.Id} not found.");
            if (existing.Version != expectedVersion)
                throw new ConcurrencyConflictException($"Control check {check.Id} version {existing.Version} != expected {expectedVersion}.");
            if (_byId.Values.Any(c => c.Id != check.Id && c.Key == check.Key))
                throw new DuplicateKeyException($"Control check '{check.Key}' already exists.");

            var updated = check with { UpdatedAt = DateTimeOffset.UtcNow, Version = existing.Version + 1 };
            _byId[updated.Id] = updated;
            return Task.FromResult(updated);
        }
    }

    public Task<bool> DeleteAsync(Guid id, CancellationToken ct = default) =>
        Task.FromResult(_byId.TryRemove(id, out _));

    public Task TouchLastRunAsync(Guid id, DateTimeOffset at, CheckRunOutcome outcome, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_byId.TryGetValue(id, out var c))
                _byId[id] = c with { LastRunAt = at, LastOutcome = outcome };
        }
        return Task.CompletedTask;
    }
}
