using System.Collections.Concurrent;
using DiffPdf.Core.Models;

namespace DiffPdf.Persistence;

/// <summary>Thread-safe in-memory folder-watch store for single-instance / dev use. Does not survive restart.</summary>
public sealed class InMemoryWatchStore : IWatchStore
{
    private readonly ConcurrentDictionary<Guid, FolderWatch> _byId = new();
    private readonly object _gate = new();

    public Task<FolderWatch> UpsertAsync(FolderWatch watch, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var existing = _byId.Values.FirstOrDefault(w => w.InstanceId == watch.InstanceId);
            var result = existing is null
                ? watch with { Version = 1, CreatedAt = DateTimeOffset.UtcNow }
                : existing with
                {
                    StabilitySeconds = watch.StabilitySeconds,
                    Enabled = watch.Enabled,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    Version = existing.Version + 1,
                };
            _byId[result.Id] = result;
            return Task.FromResult(result);
        }
    }

    public Task<FolderWatch?> GetByInstanceAsync(Guid instanceId, CancellationToken ct = default) =>
        Task.FromResult(_byId.Values.FirstOrDefault(w => w.InstanceId == instanceId));

    public Task<bool> DeleteByInstanceAsync(Guid instanceId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var existing = _byId.Values.FirstOrDefault(w => w.InstanceId == instanceId);
            return Task.FromResult(existing is not null && _byId.TryRemove(existing.Id, out _));
        }
    }

    public Task<IReadOnlyList<FolderWatch>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<FolderWatch>>(_byId.Values.OrderBy(w => w.InstanceKey).ToList());

    public Task<IReadOnlyList<FolderWatch>> ListEnabledAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<FolderWatch>>(_byId.Values.Where(w => w.Enabled).OrderBy(w => w.InstanceKey).ToList());

    public Task TouchLastTriggeredAsync(Guid id, DateTimeOffset at, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_byId.TryGetValue(id, out var w))
                _byId[id] = w with { LastTriggeredAt = at };
        }
        return Task.CompletedTask;
    }
}
