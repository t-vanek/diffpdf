using DiffPdf.Core.Models;
using DiffPdf.Core.Storage;
using DiffPdf.Persistence.SqlServer.Entities;
using DiffPdf.Persistence.SqlServer.Mapping;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace DiffPdf.Persistence.SqlServer;

/// <summary>EF Core (SQL Server) trigger store — soft delete + optimistic concurrency + single default per instance.</summary>
public sealed class SqlServerTriggerStore(DiffPdfDbContext db, EntityMapper mapper) : ITriggerStore
{
    public async Task<Trigger> CreateAsync(Trigger trigger, CancellationToken ct = default)
    {
        db.Triggers.Add(ToEntity(trigger));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
        {
            throw new DuplicateKeyException($"A default trigger already exists for instance {trigger.InstanceId}.");
        }
        return (await GetAsync(trigger.Id, ct))!;
    }

    public async Task<Trigger?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var e = await db.Triggers.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        return e is null ? null : mapper.ToDomain(e);
    }

    public async Task<Trigger?> GetDefaultForInstanceAsync(Guid instanceId, CancellationToken ct = default)
    {
        var e = await db.Triggers.AsNoTracking()
            .FirstOrDefaultAsync(x => x.InstanceId == instanceId && x.IsDefault && !x.IsDeleted, ct);
        return e is null ? null : mapper.ToDomain(e);
    }

    public async Task<IReadOnlyList<Trigger>> ListAsync(TriggerQuery query, CancellationToken ct = default)
    {
        var q = db.Triggers.AsNoTracking();
        if (!query.IncludeDeleted) q = q.Where(x => !x.IsDeleted);
        if (query.BranchId is { } b) q = q.Where(x => x.BranchId == b);
        if (query.InstanceId is { } i) q = q.Where(x => x.InstanceId == i);
        if (query.Status is { } s) { var sv = s.ToString(); q = q.Where(x => x.Status == sv); }
        var rows = await q.OrderByDescending(x => x.CreatedAt).ToListAsync(ct);
        return rows.Select(mapper.ToDomain).ToList();
    }

    public async Task<Trigger> UpdateAsync(Trigger trigger, long expectedVersion, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        int rows;
        try
        {
            rows = await db.Triggers
                .Where(x => x.Id == trigger.Id && x.Version == expectedVersion)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Name, trigger.Name)
                    .SetProperty(x => x.Description, trigger.Description)
                    .SetProperty(x => x.ActionType, trigger.ActionType.ToString())
                    .SetProperty(x => x.Status, trigger.Status.ToString())
                    .SetProperty(x => x.Enabled, trigger.Enabled)
                    .SetProperty(x => x.IsDefault, trigger.IsDefault)
                    .SetProperty(x => x.UpdatedBy, trigger.UpdatedBy)
                    .SetProperty(x => x.UpdatedAt, now)
                    .SetProperty(x => x.Version, x => x.Version + 1), ct);
        }
        catch (SqlException ex) when (ex.Number is 2601 or 2627)
        {
            throw new DuplicateKeyException($"A default trigger already exists for instance {trigger.InstanceId}.");
        }

        return rows == 0
            ? throw new ConcurrencyConflictException($"Trigger update conflict for {trigger.Id} (expected version {expectedVersion}).")
            : (await GetAsync(trigger.Id, ct))!;
    }

    public async Task<Trigger?> SetEnabledAsync(Guid id, bool enabled, string? actor, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        string status = (enabled ? TriggerStatus.Active : TriggerStatus.Disabled).ToString();
        int rows = await db.Triggers
            .Where(x => x.Id == id && !x.IsDeleted)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Enabled, enabled)
                .SetProperty(x => x.Status, status)
                .SetProperty(x => x.UpdatedBy, actor)
                .SetProperty(x => x.UpdatedAt, now)
                .SetProperty(x => x.Version, x => x.Version + 1), ct);
        return rows == 0 ? null : await GetAsync(id, ct);
    }

    public async Task<Trigger?> SoftDeleteAsync(Guid id, string? actor, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        string deleted = TriggerStatus.Deleted.ToString();
        await db.Triggers
            .Where(x => x.Id == id && !x.IsDeleted)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.IsDeleted, true)
                .SetProperty(x => x.Enabled, false)
                .SetProperty(x => x.Status, deleted)
                .SetProperty(x => x.DeletedAt, now)
                .SetProperty(x => x.UpdatedBy, actor)
                .SetProperty(x => x.UpdatedAt, now)
                .SetProperty(x => x.Version, x => x.Version + 1), ct);
        return await GetAsync(id, ct);
    }

    public async Task TouchLastRunAsync(Guid id, DateTimeOffset at, string outcome, CancellationToken ct = default)
    {
        await db.Triggers.Where(x => x.Id == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.LastRunAt, at)
                .SetProperty(x => x.LastOutcome, outcome)
                .SetProperty(x => x.RunCount, x => x.RunCount + 1), ct);
    }

    internal static TriggerEntity ToEntity(Trigger t) => new()
    {
        Id = t.Id,
        BranchId = t.BranchId,
        InstanceId = t.InstanceId,
        BranchKey = t.BranchKey,
        InstanceKey = t.InstanceKey,
        Name = t.Name,
        Description = t.Description,
        ActionType = t.ActionType.ToString(),
        Status = t.Status.ToString(),
        Enabled = t.Enabled,
        IsDefault = t.IsDefault,
        RunCount = t.RunCount,
        LastRunAt = t.LastRunAt,
        LastOutcome = t.LastOutcome,
        CreatedBy = t.CreatedBy,
        UpdatedBy = t.UpdatedBy,
        CreatedAt = t.CreatedAt,
        IsDeleted = t.IsDeleted,
        Version = 1,
    };
}
