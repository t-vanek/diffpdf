using DiffPdf.Core.Models;
using DiffPdf.Persistence.Postgres.Entities;
using DiffPdf.Persistence.Postgres.Mapping;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DiffPdf.Persistence.Postgres;

/// <summary>EF Core (PostgreSQL) folder-watch store — one watch per instance (unique by instance id).</summary>
public sealed class PostgresWatchStore(DiffPdfDbContext db, EntityMapper mapper) : IWatchStore
{
    public async Task<FolderWatch> UpsertAsync(FolderWatch watch, CancellationToken ct = default)
    {
        var existing = await db.Watches.AsNoTracking().FirstOrDefaultAsync(w => w.InstanceId == watch.InstanceId, ct);
        if (existing is not null)
        {
            await db.Watches.Where(w => w.Id == existing.Id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(w => w.StabilitySeconds, watch.StabilitySeconds)
                    .SetProperty(w => w.Enabled, watch.Enabled)
                    .SetProperty(w => w.UpdatedAt, DateTimeOffset.UtcNow)
                    .SetProperty(w => w.Version, w => w.Version + 1), ct);
            return (await GetByInstanceAsync(watch.InstanceId, ct))!;
        }

        db.Watches.Add(ToEntity(watch));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // A concurrent upsert inserted the instance's watch first; return whatever now exists.
        }
        return (await GetByInstanceAsync(watch.InstanceId, ct))!;
    }

    public async Task<FolderWatch?> GetByInstanceAsync(Guid instanceId, CancellationToken ct = default)
    {
        var e = await db.Watches.AsNoTracking().FirstOrDefaultAsync(x => x.InstanceId == instanceId, ct);
        return e is null ? null : mapper.ToDomain(e);
    }

    public async Task<bool> DeleteByInstanceAsync(Guid instanceId, CancellationToken ct = default)
    {
        int rows = await db.Watches.Where(x => x.InstanceId == instanceId).ExecuteDeleteAsync(ct);
        return rows > 0;
    }

    public async Task<IReadOnlyList<FolderWatch>> ListAsync(CancellationToken ct = default)
    {
        var rows = await db.Watches.AsNoTracking().OrderBy(x => x.InstanceKey).ToListAsync(ct);
        return rows.Select(mapper.ToDomain).ToList();
    }

    public async Task<IReadOnlyList<FolderWatch>> ListEnabledAsync(CancellationToken ct = default)
    {
        var rows = await db.Watches.AsNoTracking().Where(x => x.Enabled).OrderBy(x => x.InstanceKey).ToListAsync(ct);
        return rows.Select(mapper.ToDomain).ToList();
    }

    public async Task TouchLastTriggeredAsync(Guid id, DateTimeOffset at, CancellationToken ct = default)
    {
        await db.Watches.Where(x => x.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.LastTriggeredAt, at), ct);
    }

    internal static WatchEntity ToEntity(FolderWatch w) => new()
    {
        Id = w.Id,
        BranchId = w.BranchId,
        InstanceId = w.InstanceId,
        BranchKey = w.BranchKey,
        InstanceKey = w.InstanceKey,
        StabilitySeconds = w.StabilitySeconds,
        Enabled = w.Enabled,
        CreatedAt = w.CreatedAt,
        LastTriggeredAt = w.LastTriggeredAt,
        Version = 1,
    };
}
