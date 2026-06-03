using System.Collections.Concurrent;
using DiffPdf.Core.Models;
using DiffPdf.Core.Storage;

namespace DiffPdf.Persistence;

public sealed class InMemoryBranchStore : IBranchStore
{
    private readonly ConcurrentDictionary<string, Branch> _byKey = new(StringComparer.Ordinal);

    public Task<Branch> CreateAsync(string key, string name, CancellationToken ct = default)
    {
        var branch = new Branch { Id = Guid.NewGuid(), Key = key, Name = name };
        if (!_byKey.TryAdd(key, branch))
            throw new DuplicateKeyException($"Branch '{key}' already exists.");
        return Task.FromResult(branch);
    }

    public Task<Branch?> GetByKeyAsync(string key, CancellationToken ct = default) =>
        Task.FromResult(_byKey.TryGetValue(key, out var b) ? b : null);

    public Task<IReadOnlyList<Branch>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Branch>>(_byKey.Values.OrderBy(b => b.Key).ToList());

    public Task<bool> DeleteByKeyAsync(string key, CancellationToken ct = default) =>
        Task.FromResult(_byKey.TryRemove(key, out _));
}

public sealed class InMemoryInstanceStore : IInstanceStore
{
    // key: (branchId, instanceKey)
    private readonly ConcurrentDictionary<(Guid, string), ComparisonInstance> _byKey = new();

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

    public Task<ComparisonInstance?> GetByKeyAsync(Guid branchId, string key, CancellationToken ct = default) =>
        Task.FromResult(_byKey.TryGetValue((branchId, key), out var i) ? i : null);

    public Task<IReadOnlyList<ComparisonInstance>> ListAsync(Guid branchId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ComparisonInstance>>(
            _byKey.Values.Where(i => i.BranchId == branchId).OrderBy(i => i.Key).ToList());

    public Task<bool> DeleteByKeyAsync(Guid branchId, string key, CancellationToken ct = default) =>
        Task.FromResult(_byKey.TryRemove((branchId, key), out _));
}
