using System.Collections.Concurrent;
using DiffPdf.Core.Models;
using DiffPdf.Core.Storage;

namespace DiffPdf.Persistence;

/// <summary>
/// Thread-safe in-memory automation store for single-instance / dev use. Mirrors the duplicate-key,
/// optimistic-concurrency and atomic engine-state semantics of the relational store. Does not survive
/// restart.
/// </summary>
public sealed class InMemoryAutomationStore : IAutomationStore
{
    private readonly ConcurrentDictionary<Guid, Automation> _byId = new();
    private readonly object _gate = new();

    public Task<Automation> CreateAsync(Automation automation, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_byId.Values.Any(a => a.Key == automation.Key))
                throw new DuplicateKeyException($"Automation '{automation.Key}' already exists.");

            var created = automation with { Version = 1 };
            _byId[created.Id] = created;
            return Task.FromResult(created);
        }
    }

    public Task<Automation?> GetAsync(Guid id, CancellationToken ct = default) =>
        Task.FromResult(_byId.TryGetValue(id, out var a) ? a : null);

    public Task<Automation?> GetByKeyAsync(string key, CancellationToken ct = default) =>
        Task.FromResult(_byId.Values.FirstOrDefault(a => a.Key == key));

    public Task<IReadOnlyList<Automation>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Automation>>(_byId.Values.OrderBy(a => a.Key).ToList());

    public Task<IReadOnlyList<Automation>> ListEnabledAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Automation>>(
            _byId.Values.Where(a => a.Enabled).OrderBy(a => a.Key).ToList());

    public Task<Automation> UpdateAsync(Automation automation, long expectedVersion, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_byId.TryGetValue(automation.Id, out var existing))
                throw new ConcurrencyConflictException($"Automation {automation.Id} not found.");
            if (existing.Version != expectedVersion)
                throw new ConcurrencyConflictException($"Automation {automation.Id} version {existing.Version} != expected {expectedVersion}.");
            if (_byId.Values.Any(a => a.Id != automation.Id && a.Key == automation.Key))
                throw new DuplicateKeyException($"Automation '{automation.Key}' already exists.");

            var updated = automation with { UpdatedAt = DateTimeOffset.UtcNow, Version = existing.Version + 1 };
            _byId[updated.Id] = updated;
            return Task.FromResult(updated);
        }
    }

    public Task<bool> DeleteAsync(Guid id, CancellationToken ct = default) =>
        Task.FromResult(_byId.TryRemove(id, out _));

    public Task<bool> TryBeginRunAsync(Guid id, DateTimeOffset now, DateTimeOffset? advanceNextRunAtTo, TimeSpan staleAfter, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_byId.TryGetValue(id, out var a))
                return Task.FromResult(false);
            if (a.RunningSince is { } since && since >= now - staleAfter)
                return Task.FromResult(false);

            _byId[id] = a with
            {
                RunningSince = now,
                NextRunAt = advanceNextRunAtTo ?? a.NextRunAt,
            };
            return Task.FromResult(true);
        }
    }

    public Task CompleteRunAsync(Guid id, DateTimeOffset at, AutomationRunOutcome outcome, int consecutiveFailures, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_byId.TryGetValue(id, out var a))
                _byId[id] = a with
                {
                    RunningSince = null,
                    LastRunAt = at,
                    LastOutcome = outcome,
                    ConsecutiveFailures = consecutiveFailures,
                };
        }
        return Task.CompletedTask;
    }

    public Task SetNextRunIfNullAsync(Guid id, DateTimeOffset next, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_byId.TryGetValue(id, out var a) && a.NextRunAt is null)
                _byId[id] = a with { NextRunAt = next };
        }
        return Task.CompletedTask;
    }

    public Task<bool> TryClaimEventTriggerAsync(Guid id, DateTimeOffset now, TimeSpan debounce, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_byId.TryGetValue(id, out var a) || !a.Enabled)
                return Task.FromResult(false);
            if (a.LastEventTriggeredAt is { } last && last > now - debounce)
                return Task.FromResult(false);

            _byId[id] = a with { LastEventTriggeredAt = now };
            return Task.FromResult(true);
        }
    }
}
