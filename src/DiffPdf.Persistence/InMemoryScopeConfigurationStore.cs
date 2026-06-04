using System.Collections.Concurrent;
using DiffPdf.Core.Models;
using DiffPdf.Core.Storage;

namespace DiffPdf.Persistence;

/// <summary>
/// Thread-safe in-memory scope-configuration store for single-instance / dev use. Mirrors the
/// duplicate-target + optimistic-concurrency semantics of the relational stores (at most one global
/// row, one row per branch, one row per instance). Does not survive restart.
/// </summary>
public sealed class InMemoryScopeConfigurationStore : IScopeConfigurationStore
{
    private readonly ConcurrentDictionary<Guid, ScopeConfiguration> _byId = new();
    private readonly object _gate = new();

    public Task<ScopeConfiguration> CreateAsync(ScopeConfiguration config, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (Conflicts(config, exceptId: null))
                throw new DuplicateKeyException($"A configuration for this scope ({Describe(config)}) already exists.");

            var created = config with { Version = 1 };
            _byId[created.Id] = created;
            return Task.FromResult(created);
        }
    }

    public Task<ScopeConfiguration?> GetGlobalAsync(CancellationToken ct = default) =>
        Task.FromResult(_byId.Values.FirstOrDefault(c => c.Level == ConfigScopeLevel.Global));

    public Task<ScopeConfiguration?> GetByBranchAsync(Guid branchId, CancellationToken ct = default) =>
        Task.FromResult(_byId.Values.FirstOrDefault(c => c.Level == ConfigScopeLevel.Branch && c.BranchId == branchId));

    public Task<ScopeConfiguration?> GetByInstanceAsync(Guid instanceId, CancellationToken ct = default) =>
        Task.FromResult(_byId.Values.FirstOrDefault(c => c.Level == ConfigScopeLevel.Instance && c.InstanceId == instanceId));

    public Task<ScopeConfiguration> UpdateAsync(ScopeConfiguration config, long expectedVersion, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_byId.TryGetValue(config.Id, out var existing))
                throw new ConcurrencyConflictException($"Scope configuration {config.Id} not found.");
            if (existing.Version != expectedVersion)
                throw new ConcurrencyConflictException($"Scope configuration {config.Id} version {existing.Version} != expected {expectedVersion}.");

            var updated = config with { UpdatedAt = DateTimeOffset.UtcNow, Version = existing.Version + 1 };
            _byId[updated.Id] = updated;
            return Task.FromResult(updated);
        }
    }

    public Task<bool> DeleteByBranchAsync(Guid branchId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var row = _byId.Values.FirstOrDefault(c => c.Level == ConfigScopeLevel.Branch && c.BranchId == branchId);
            return Task.FromResult(row is not null && _byId.TryRemove(row.Id, out _));
        }
    }

    public Task<bool> DeleteByInstanceAsync(Guid instanceId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var row = _byId.Values.FirstOrDefault(c => c.Level == ConfigScopeLevel.Instance && c.InstanceId == instanceId);
            return Task.FromResult(row is not null && _byId.TryRemove(row.Id, out _));
        }
    }

    private bool Conflicts(ScopeConfiguration config, Guid? exceptId) =>
        _byId.Values.Any(c => (exceptId is null || c.Id != exceptId) && c.Level == config.Level && config.Level switch
        {
            ConfigScopeLevel.Global => true,
            ConfigScopeLevel.Branch => c.BranchId == config.BranchId,
            ConfigScopeLevel.Instance => c.InstanceId == config.InstanceId,
            _ => false,
        });

    private static string Describe(ScopeConfiguration c) => c.Level switch
    {
        ConfigScopeLevel.Branch => $"branch {c.BranchId}",
        ConfigScopeLevel.Instance => $"instance {c.InstanceId}",
        _ => "global",
    };
}
