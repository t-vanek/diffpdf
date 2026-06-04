using DiffPdf.Core.Models;

namespace DiffPdf.Persistence;

/// <summary>Filter for listing API keys (deleted excluded unless requested; disabled always listed for admin).</summary>
public sealed record ApiKeyQuery
{
    public bool IncludeDeleted { get; init; }
}

/// <summary>
/// Persistence for third-party API keys. Soft delete only. Mutations use optimistic concurrency via
/// <c>Version</c>. Only the SHA-256 hash is stored; <see cref="FindByHashAsync"/> is the auth-time lookup.
/// </summary>
public interface IApiKeyStore
{
    /// <summary>Creates a key; throws <see cref="DiffPdf.Core.Storage.DuplicateKeyException"/> on a hash collision.</summary>
    Task<ApiKey> CreateAsync(ApiKey key, CancellationToken ct = default);

    /// <summary>Gets a key by id, including soft-deleted ones (so detail/audit stays reachable).</summary>
    Task<ApiKey?> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>Finds a key by its SHA-256 hash (any state); the caller validates usability. Null if unknown.</summary>
    Task<ApiKey?> FindByHashAsync(string keyHash, CancellationToken ct = default);

    Task<IReadOnlyList<ApiKey>> ListAsync(ApiKeyQuery query, CancellationToken ct = default);

    Task<ApiKey> UpdateAsync(ApiKey key, long expectedVersion, CancellationToken ct = default);

    /// <summary>Revokes a key (RevokedAt set, Enabled=false). Returns the key, or null if missing.</summary>
    Task<ApiKey?> RevokeAsync(Guid id, string? actor, CancellationToken ct = default);

    /// <summary>Soft-deletes a key (IsDeleted=true). Returns the deleted key, or null if missing.</summary>
    Task<ApiKey?> SoftDeleteAsync(Guid id, string? actor, CancellationToken ct = default);

    /// <summary>Records last successful use (best-effort, no version guard).</summary>
    Task TouchLastUsedAsync(Guid id, DateTimeOffset at, CancellationToken ct = default);
}
