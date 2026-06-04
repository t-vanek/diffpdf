using System.Collections.Concurrent;
using DiffPdf.Core.Models;
using DiffPdf.Core.Storage;

namespace DiffPdf.Persistence;

/// <summary>Thread-safe in-memory API key store for dev/test. Mirrors the relational unique-hash +
/// soft-delete + optimistic-concurrency semantics.</summary>
public sealed class InMemoryApiKeyStore : IApiKeyStore
{
    private readonly ConcurrentDictionary<Guid, ApiKey> _byId = new();
    private readonly object _gate = new();

    public Task<ApiKey> CreateAsync(ApiKey key, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_byId.Values.Any(k => k.KeyHash == key.KeyHash))
                throw new DuplicateKeyException("An API key with the same hash already exists.");
            var created = key with { Version = 1 };
            _byId[created.Id] = created;
            return Task.FromResult(created);
        }
    }

    public Task<ApiKey?> GetAsync(Guid id, CancellationToken ct = default) =>
        Task.FromResult(_byId.TryGetValue(id, out var k) ? k : null);

    public Task<ApiKey?> FindByHashAsync(string keyHash, CancellationToken ct = default) =>
        Task.FromResult(_byId.Values.FirstOrDefault(k => k.KeyHash == keyHash));

    public Task<IReadOnlyList<ApiKey>> ListAsync(ApiKeyQuery query, CancellationToken ct = default)
    {
        IEnumerable<ApiKey> q = _byId.Values;
        if (!query.IncludeDeleted) q = q.Where(k => !k.IsDeleted);
        return Task.FromResult<IReadOnlyList<ApiKey>>(q.OrderByDescending(k => k.CreatedAt).ToList());
    }

    public Task<ApiKey> UpdateAsync(ApiKey key, long expectedVersion, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_byId.TryGetValue(key.Id, out var existing))
                throw new ConcurrencyConflictException($"API key {key.Id} not found.");
            if (existing.Version != expectedVersion)
                throw new ConcurrencyConflictException($"API key {key.Id} version {existing.Version} != expected {expectedVersion}.");

            var updated = key with { UpdatedAt = DateTimeOffset.UtcNow, Version = existing.Version + 1 };
            _byId[updated.Id] = updated;
            return Task.FromResult(updated);
        }
    }

    public Task<ApiKey?> RevokeAsync(Guid id, string? actor, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_byId.TryGetValue(id, out var k)) return Task.FromResult<ApiKey?>(null);
            if (k.RevokedAt is not null) return Task.FromResult<ApiKey?>(k);
            var revoked = k with
            {
                RevokedAt = DateTimeOffset.UtcNow,
                Enabled = false,
                UpdatedAt = DateTimeOffset.UtcNow,
                UpdatedBy = actor ?? k.UpdatedBy,
                RevokedBy = actor,
                Version = k.Version + 1,
            };
            _byId[id] = revoked;
            return Task.FromResult<ApiKey?>(revoked);
        }
    }

    public Task<ApiKey?> SoftDeleteAsync(Guid id, string? actor, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_byId.TryGetValue(id, out var k)) return Task.FromResult<ApiKey?>(null);
            if (k.IsDeleted) return Task.FromResult<ApiKey?>(k);
            var deleted = k with
            {
                IsDeleted = true,
                Enabled = false,
                DeletedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                UpdatedBy = actor ?? k.UpdatedBy,
                Version = k.Version + 1,
            };
            _byId[id] = deleted;
            return Task.FromResult<ApiKey?>(deleted);
        }
    }

    public Task TouchLastUsedAsync(Guid id, DateTimeOffset at, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_byId.TryGetValue(id, out var k))
                _byId[id] = k with { LastUsedAt = at };
        }
        return Task.CompletedTask;
    }
}
