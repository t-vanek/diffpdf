using System.Collections.Concurrent;
using DiffPdf.Core.Models;
using DiffPdf.Core.Storage;

namespace DiffPdf.Persistence;

public sealed class InMemoryBranchStore : IBranchStore
{
    private readonly ConcurrentDictionary<string, Branch> _byKey = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public Task<Branch> CreateAsync(string key, string name, CancellationToken ct = default)
    {
        var branch = new Branch { Id = Guid.NewGuid(), Key = key, Name = name };
        if (!_byKey.TryAdd(key, branch))
            throw new DuplicateKeyException($"Branch '{key}' already exists.");
        return Task.FromResult(branch);
    }

    public Task<Branch> UpdateAsync(Branch branch, long expectedVersion, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_byKey.TryGetValue(branch.Key, out var existing))
                throw new ConcurrencyConflictException($"Branch '{branch.Key}' not found.");
            if (existing.Version != expectedVersion)
                throw new ConcurrencyConflictException($"Branch '{branch.Key}' version {existing.Version} != expected {expectedVersion}.");

            var updated = existing with
            {
                Name = branch.Name,
                Enabled = branch.Enabled,
                UpdatedAt = DateTimeOffset.UtcNow,
                Version = existing.Version + 1,
            };
            _byKey[branch.Key] = updated;
            return Task.FromResult(updated);
        }
    }

    public Task<Branch?> GetByKeyAsync(string key, CancellationToken ct = default) =>
        Task.FromResult(_byKey.TryGetValue(key, out var b) ? b : null);

    public Task<Branch?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        Task.FromResult<Branch?>(_byKey.Values.FirstOrDefault(b => b.Id == id));

    public Task<IReadOnlyList<Branch>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Branch>>(_byKey.Values.OrderBy(b => b.Key).ToList());

    public Task SetQueuePausedAsync(Guid branchId, bool paused, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var existing = _byKey.Values.FirstOrDefault(b => b.Id == branchId);
            if (existing is not null)
                _byKey[existing.Key] = existing with { QueuePaused = paused, UpdatedAt = DateTimeOffset.UtcNow };
        }
        return Task.CompletedTask;
    }

    public Task<bool> DeleteByKeyAsync(string key, CancellationToken ct = default) =>
        Task.FromResult(_byKey.TryRemove(key, out _));
}

public sealed class InMemoryInstanceStore : IInstanceStore
{
    // key: (branchId, instanceKey)
    private readonly ConcurrentDictionary<(Guid, string), ComparisonInstance> _byKey = new();
    private readonly object _gate = new();

    public Task<ComparisonInstance> CreateAsync(
        Guid branchId, string key, string name, string basePath, string? credentialProfile, CancellationToken ct = default)
    {
        var instance = new ComparisonInstance
        {
            Id = Guid.NewGuid(),
            BranchId = branchId,
            Key = key,
            Name = name,
            BasePath = basePath,
            CredentialProfile = credentialProfile,
        };
        if (!_byKey.TryAdd((branchId, key), instance))
            throw new DuplicateKeyException($"Instance '{key}' already exists in this branch.");
        return Task.FromResult(instance);
    }

    public Task<ComparisonInstance> UpdateAsync(ComparisonInstance instance, long expectedVersion, CancellationToken ct = default)
    {
        var compositeKey = (instance.BranchId, instance.Key);
        lock (_gate)
        {
            if (!_byKey.TryGetValue(compositeKey, out var existing))
                throw new ConcurrencyConflictException($"Instance '{instance.Key}' not found in this branch.");
            if (existing.Version != expectedVersion)
                throw new ConcurrencyConflictException($"Instance '{instance.Key}' version {existing.Version} != expected {expectedVersion}.");

            var updated = existing with
            {
                Name = instance.Name,
                BasePath = instance.BasePath,
                CredentialProfile = instance.CredentialProfile,
                Enabled = instance.Enabled,
                UpdatedAt = DateTimeOffset.UtcNow,
                Version = existing.Version + 1,
            };
            _byKey[compositeKey] = updated;
            return Task.FromResult(updated);
        }
    }

    public Task<ComparisonInstance?> GetByKeyAsync(Guid branchId, string key, CancellationToken ct = default) =>
        Task.FromResult(_byKey.TryGetValue((branchId, key), out var i) ? i : null);

    public Task<IReadOnlyList<ComparisonInstance>> ListAsync(Guid branchId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ComparisonInstance>>(
            _byKey.Values.Where(i => i.BranchId == branchId).OrderBy(i => i.Key).ToList());

    public Task<bool> DeleteByKeyAsync(Guid branchId, string key, CancellationToken ct = default) =>
        Task.FromResult(_byKey.TryRemove((branchId, key), out _));
}
