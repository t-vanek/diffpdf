using DiffPdf.Core.Models;
using DiffPdf.Core.Storage;
using DiffPdf.Persistence.Postgres.Entities;
using DiffPdf.Persistence.Postgres.Mapping;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DiffPdf.Persistence.Postgres;

/// <summary>EF Core (PostgreSQL) control-check store — runtime-managed control/monitoring definitions.</summary>
public sealed class PostgresControlCheckStore(DiffPdfDbContext db, EntityMapper mapper) : IControlCheckStore
{
    public async Task<ControlCheck> CreateAsync(ControlCheck check, CancellationToken ct = default)
    {
        db.ControlChecks.Add(ToEntity(check));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw new DuplicateKeyException($"Control check '{check.Key}' already exists.");
        }
        return (await GetAsync(check.Id, ct))!;
    }

    public async Task<ControlCheck?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var e = await db.ControlChecks.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        return e is null ? null : mapper.ToDomain(e);
    }

    public async Task<ControlCheck?> GetByKeyAsync(string key, CancellationToken ct = default)
    {
        var e = await db.ControlChecks.AsNoTracking().FirstOrDefaultAsync(x => x.Key == key, ct);
        return e is null ? null : mapper.ToDomain(e);
    }

    public async Task<IReadOnlyList<ControlCheck>> ListAsync(CancellationToken ct = default)
    {
        var rows = await db.ControlChecks.AsNoTracking().OrderBy(x => x.Key).ToListAsync(ct);
        return rows.Select(mapper.ToDomain).ToList();
    }

    public async Task<IReadOnlyList<ControlCheck>> ListEnabledAsync(CancellationToken ct = default)
    {
        var rows = await db.ControlChecks.AsNoTracking()
            .Where(x => x.Enabled).OrderBy(x => x.Key).ToListAsync(ct);
        return rows.Select(mapper.ToDomain).ToList();
    }

    public async Task<ControlCheck> UpdateAsync(ControlCheck check, long expectedVersion, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        string parametersJson = DiffPdfJson.Serialize(check.Parameters);
        string eventsJson = DiffPdfJson.Serialize(check.Events);

        int rows;
        try
        {
            // ExecuteUpdate runs SQL directly, so a unique-key violation surfaces as the raw
            // PostgresException (not wrapped in DbUpdateException like SaveChanges).
            rows = await db.ControlChecks
                .Where(x => x.Id == check.Id && x.Version == expectedVersion)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Key, check.Key)
                    .SetProperty(x => x.Name, check.Name)
                    .SetProperty(x => x.Type, check.Type.ToString())
                    .SetProperty(x => x.ScopeKind, check.ScopeKind.ToString())
                    .SetProperty(x => x.BranchKey, check.BranchKey)
                    .SetProperty(x => x.InstanceKey, check.InstanceKey)
                    .SetProperty(x => x.Cron, check.Cron)
                    .SetProperty(x => x.IntervalSeconds, check.IntervalSeconds)
                    .SetProperty(x => x.ParametersJson, parametersJson)
                    .SetProperty(x => x.EventsJson, eventsJson)
                    .SetProperty(x => x.Enabled, check.Enabled)
                    .SetProperty(x => x.UpdatedAt, now)
                    .SetProperty(x => x.Version, x => x.Version + 1), ct);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new DuplicateKeyException($"Control check '{check.Key}' already exists.");
        }

        return rows == 0
            ? throw new ConcurrencyConflictException($"Control check update conflict for {check.Id} (expected version {expectedVersion}).")
            : (await GetAsync(check.Id, ct))!;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        int rows = await db.ControlChecks.Where(x => x.Id == id).ExecuteDeleteAsync(ct);
        return rows > 0;
    }

    public async Task TouchLastRunAsync(Guid id, DateTimeOffset at, CheckRunOutcome outcome, CancellationToken ct = default)
    {
        await db.ControlChecks.Where(x => x.Id == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.LastRunAt, at)
                .SetProperty(x => x.LastOutcome, outcome.ToString()), ct);
    }

    internal static ControlCheckEntity ToEntity(ControlCheck c) => new()
    {
        Id = c.Id,
        Key = c.Key,
        Name = c.Name,
        Type = c.Type.ToString(),
        ScopeKind = c.ScopeKind.ToString(),
        BranchKey = c.BranchKey,
        InstanceKey = c.InstanceKey,
        Cron = c.Cron,
        IntervalSeconds = c.IntervalSeconds,
        ParametersJson = DiffPdfJson.Serialize(c.Parameters),
        EventsJson = DiffPdfJson.Serialize(c.Events),
        Enabled = c.Enabled,
        CreatedAt = c.CreatedAt,
        LastRunAt = c.LastRunAt,
        LastOutcome = c.LastOutcome?.ToString(),
        Version = 1,
    };
}
