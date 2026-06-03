using System.Collections.Concurrent;
using DiffPdf.Core.Models;
using DiffPdf.Core.Storage;

namespace DiffPdf.Persistence;

/// <summary>
/// Thread-safe in-memory schedule store for single-instance / dev use. Mirrors the
/// duplicate-key + optimistic-concurrency semantics of the relational stores. Does not
/// survive restart.
/// </summary>
public sealed class InMemoryScheduleStore : IScheduleStore
{
    private readonly ConcurrentDictionary<Guid, ComparisonSchedule> _byId = new();
    private readonly object _gate = new();

    public Task<ComparisonSchedule> CreateAsync(ComparisonSchedule schedule, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_byId.Values.Any(s => s.InstanceId == schedule.InstanceId && s.Key == schedule.Key))
                throw new DuplicateKeyException($"Schedule '{schedule.Key}' already exists in this instance.");

            var created = schedule with { Version = 1 };
            _byId[created.Id] = created;
            return Task.FromResult(created);
        }
    }

    public Task<ComparisonSchedule?> GetAsync(Guid id, CancellationToken ct = default) =>
        Task.FromResult(_byId.TryGetValue(id, out var s) ? s : null);

    public Task<ComparisonSchedule?> GetByKeyAsync(Guid instanceId, string key, CancellationToken ct = default) =>
        Task.FromResult(_byId.Values.FirstOrDefault(s => s.InstanceId == instanceId && s.Key == key));

    public Task<IReadOnlyList<ComparisonSchedule>> ListByInstanceAsync(Guid instanceId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ComparisonSchedule>>(
            _byId.Values.Where(s => s.InstanceId == instanceId).OrderBy(s => s.Key).ToList());

    public Task<IReadOnlyList<ComparisonSchedule>> ListEnabledAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ComparisonSchedule>>(
            _byId.Values.Where(s => s.Enabled).OrderBy(s => s.Key).ToList());

    public Task<ComparisonSchedule> UpdateAsync(ComparisonSchedule schedule, long expectedVersion, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_byId.TryGetValue(schedule.Id, out var existing))
                throw new ConcurrencyConflictException($"Schedule {schedule.Id} not found.");
            if (existing.Version != expectedVersion)
                throw new ConcurrencyConflictException($"Schedule {schedule.Id} version {existing.Version} != expected {expectedVersion}.");
            if (_byId.Values.Any(s => s.Id != schedule.Id && s.InstanceId == schedule.InstanceId && s.Key == schedule.Key))
                throw new DuplicateKeyException($"Schedule '{schedule.Key}' already exists in this instance.");

            var updated = schedule with { UpdatedAt = DateTimeOffset.UtcNow, Version = existing.Version + 1 };
            _byId[updated.Id] = updated;
            return Task.FromResult(updated);
        }
    }

    public Task<bool> DeleteAsync(Guid id, CancellationToken ct = default) =>
        Task.FromResult(_byId.TryRemove(id, out _));

    public Task TouchLastRunAsync(Guid id, DateTimeOffset at, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_byId.TryGetValue(id, out var s))
                _byId[id] = s with { LastRunAt = at };
        }
        return Task.CompletedTask;
    }
}
