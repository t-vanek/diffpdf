using DiffPdf.Core.Models;
using DiffPdf.Core.Storage;
using DiffPdf.Persistence.SqlServer.Entities;
using DiffPdf.Persistence.SqlServer.Mapping;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace DiffPdf.Persistence.SqlServer;

/// <summary>EF Core (SQL Server) API key store — unique hash + soft delete + optimistic concurrency.</summary>
public sealed class SqlServerApiKeyStore(DiffPdfDbContext db, EntityMapper mapper) : IApiKeyStore
{
    public async Task<ApiKey> CreateAsync(ApiKey key, CancellationToken ct = default)
    {
        db.ApiKeys.Add(ToEntity(key));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
        {
            throw new DuplicateKeyException("An API key with the same hash already exists.");
        }
        return (await GetAsync(key.Id, ct))!;
    }

    public async Task<ApiKey?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var e = await db.ApiKeys.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        return e is null ? null : mapper.ToDomain(e);
    }

    public async Task<ApiKey?> FindByHashAsync(string keyHash, CancellationToken ct = default)
    {
        var e = await db.ApiKeys.AsNoTracking().FirstOrDefaultAsync(x => x.KeyHash == keyHash, ct);
        return e is null ? null : mapper.ToDomain(e);
    }

    public async Task<IReadOnlyList<ApiKey>> ListAsync(ApiKeyQuery query, CancellationToken ct = default)
    {
        var q = db.ApiKeys.AsNoTracking();
        if (!query.IncludeDeleted) q = q.Where(x => !x.IsDeleted);
        var rows = await q.OrderByDescending(x => x.CreatedAt).ToListAsync(ct);
        return rows.Select(mapper.ToDomain).ToList();
    }

    public async Task<ApiKey> UpdateAsync(ApiKey key, long expectedVersion, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        string role = key.Role.ToString();
        int rows = await db.ApiKeys
            .Where(x => x.Id == key.Id && x.Version == expectedVersion)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Name, key.Name)
                .SetProperty(x => x.Role, role)
                .SetProperty(x => x.Enabled, key.Enabled)
                .SetProperty(x => x.ExpiresAt, key.ExpiresAt)
                .SetProperty(x => x.UpdatedBy, key.UpdatedBy)
                .SetProperty(x => x.UpdatedAt, now)
                .SetProperty(x => x.Version, x => x.Version + 1), ct);

        return rows == 0
            ? throw new ConcurrencyConflictException($"API key update conflict for {key.Id} (expected version {expectedVersion}).")
            : (await GetAsync(key.Id, ct))!;
    }

    public async Task<ApiKey?> RevokeAsync(Guid id, string? actor, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        await db.ApiKeys
            .Where(x => x.Id == id && x.RevokedAt == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.RevokedAt, now)
                .SetProperty(x => x.Enabled, false)
                .SetProperty(x => x.RevokedBy, actor)
                .SetProperty(x => x.UpdatedBy, actor)
                .SetProperty(x => x.UpdatedAt, now)
                .SetProperty(x => x.Version, x => x.Version + 1), ct);
        return await GetAsync(id, ct);
    }

    public async Task<ApiKey?> SoftDeleteAsync(Guid id, string? actor, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        await db.ApiKeys
            .Where(x => x.Id == id && !x.IsDeleted)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.IsDeleted, true)
                .SetProperty(x => x.Enabled, false)
                .SetProperty(x => x.DeletedAt, now)
                .SetProperty(x => x.UpdatedBy, actor)
                .SetProperty(x => x.UpdatedAt, now)
                .SetProperty(x => x.Version, x => x.Version + 1), ct);
        return await GetAsync(id, ct);
    }

    public async Task TouchLastUsedAsync(Guid id, DateTimeOffset at, CancellationToken ct = default)
    {
        await db.ApiKeys.Where(x => x.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.LastUsedAt, at), ct);
    }

    internal static ApiKeyEntity ToEntity(ApiKey k) => new()
    {
        Id = k.Id,
        Name = k.Name,
        KeyHash = k.KeyHash,
        Prefix = k.Prefix,
        Role = k.Role.ToString(),
        Enabled = k.Enabled,
        ExpiresAt = k.ExpiresAt,
        CreatedBy = k.CreatedBy,
        CreatedAt = k.CreatedAt,
        UpdatedBy = k.UpdatedBy,
        UpdatedAt = k.UpdatedAt,
        LastUsedAt = k.LastUsedAt,
        RevokedAt = k.RevokedAt,
        RevokedBy = k.RevokedBy,
        IsDeleted = k.IsDeleted,
        DeletedAt = k.DeletedAt,
        Version = 1,
    };
}
