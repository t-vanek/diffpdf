using System.Collections.Concurrent;
using DiffPdf.Core.Models;
using DiffPdf.Core.Storage;

namespace DiffPdf.Persistence;

public sealed class InMemoryBusinessInstanceStore : IBusinessInstanceStore
{
    private readonly ConcurrentDictionary<string, BusinessInstance> _byKey = new(StringComparer.Ordinal);

    public Task<BusinessInstance> CreateAsync(string key, string name, CancellationToken ct = default)
    {
        var instance = new BusinessInstance { Id = Guid.NewGuid(), Key = key, Name = name };
        if (!_byKey.TryAdd(key, instance))
            throw new DuplicateKeyException($"Business instance '{key}' already exists.");
        return Task.FromResult(instance);
    }

    public Task<BusinessInstance?> GetByKeyAsync(string key, CancellationToken ct = default) =>
        Task.FromResult(_byKey.TryGetValue(key, out var i) ? i : null);

    public Task<IReadOnlyList<BusinessInstance>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<BusinessInstance>>(_byKey.Values.OrderBy(i => i.Key).ToList());
}

public sealed class InMemoryProjectStore : IProjectStore
{
    // key: (businessInstanceId, projectKey)
    private readonly ConcurrentDictionary<(Guid, string), ComparisonProject> _byKey = new();

    public Task<ComparisonProject> CreateAsync(Guid businessInstanceId, string key, string name, CancellationToken ct = default)
    {
        var project = new ComparisonProject
        {
            Id = Guid.NewGuid(),
            BusinessInstanceId = businessInstanceId,
            Key = key,
            Name = name,
        };
        if (!_byKey.TryAdd((businessInstanceId, key), project))
            throw new DuplicateKeyException($"Project '{key}' already exists in this business instance.");
        return Task.FromResult(project);
    }

    public Task<ComparisonProject?> GetByKeyAsync(Guid businessInstanceId, string key, CancellationToken ct = default) =>
        Task.FromResult(_byKey.TryGetValue((businessInstanceId, key), out var p) ? p : null);

    public Task<IReadOnlyList<ComparisonProject>> ListAsync(Guid businessInstanceId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ComparisonProject>>(
            _byKey.Values.Where(p => p.BusinessInstanceId == businessInstanceId).OrderBy(p => p.Key).ToList());
}
