using DiffPdf.Core.Models;
using DiffPdf.Core.Storage;
using DiffPdf.Persistence.SqlServer.Entities;
using DiffPdf.Persistence.SqlServer.Mapping;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace DiffPdf.Persistence.SqlServer;

/// <summary>EF Core (SQL Server) schedule store — runtime-managed recurring batch definitions.</summary>
public sealed class SqlServerScheduleStore(DiffPdfDbContext db, EntityMapper mapper) : IScheduleStore
{
    public async Task<ComparisonSchedule> CreateAsync(ComparisonSchedule schedule, CancellationToken ct = default)
    {
        db.Schedules.Add(ToEntity(schedule));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
        {
            throw new DuplicateKeyException($"Schedule '{schedule.Key}' already exists in this instance.");
        }
        return (await GetAsync(schedule.Id, ct))!;
    }

    public async Task<ComparisonSchedule?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var e = await db.Schedules.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        return e is null ? null : mapper.ToDomain(e);
    }

    public async Task<ComparisonSchedule?> GetByKeyAsync(Guid instanceId, string key, CancellationToken ct = default)
    {
        var e = await db.Schedules.AsNoTracking()
            .FirstOrDefaultAsync(x => x.InstanceId == instanceId && x.Key == key, ct);
        return e is null ? null : mapper.ToDomain(e);
    }

    public async Task<IReadOnlyList<ComparisonSchedule>> ListByInstanceAsync(Guid instanceId, CancellationToken ct = default)
    {
        var rows = await db.Schedules.AsNoTracking()
            .Where(x => x.InstanceId == instanceId).OrderBy(x => x.Key).ToListAsync(ct);
        return rows.Select(mapper.ToDomain).ToList();
    }

    public async Task<IReadOnlyList<ComparisonSchedule>> ListEnabledAsync(CancellationToken ct = default)
    {
        var rows = await db.Schedules.AsNoTracking()
            .Where(x => x.Enabled).OrderBy(x => x.Key).ToListAsync(ct);
        return rows.Select(mapper.ToDomain).ToList();
    }

    public async Task<ComparisonSchedule> UpdateAsync(ComparisonSchedule schedule, long expectedVersion, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        string optionsJson = DiffPdfJson.Serialize(schedule.Options);
        string? gateJson = schedule.Gate is null ? null : DiffPdfJson.Serialize(schedule.Gate);

        int rows;
        try
        {
            // ExecuteUpdate runs SQL directly, so a unique-key violation surfaces as the raw
            // SqlException (not wrapped in DbUpdateException like SaveChanges).
            rows = await db.Schedules
                .Where(x => x.Id == schedule.Id && x.Version == expectedVersion)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Key, schedule.Key)
                    .SetProperty(x => x.Name, schedule.Name)
                    .SetProperty(x => x.Cron, schedule.Cron)
                    .SetProperty(x => x.OptionsJson, optionsJson)
                    .SetProperty(x => x.GateJson, gateJson)
                    .SetProperty(x => x.SearchPattern, schedule.SearchPattern)
                    .SetProperty(x => x.Recursive, schedule.Recursive)
                    .SetProperty(x => x.MaxDegreeOfParallelism, schedule.MaxDegreeOfParallelism)
                    .SetProperty(x => x.Enabled, schedule.Enabled)
                    .SetProperty(x => x.UpdatedAt, now)
                    .SetProperty(x => x.Version, x => x.Version + 1), ct);
        }
        catch (SqlException ex) when (ex.Number is 2601 or 2627)
        {
            throw new DuplicateKeyException($"Schedule '{schedule.Key}' already exists in this instance.");
        }

        return rows == 0
            ? throw new ConcurrencyConflictException($"Schedule update conflict for {schedule.Id} (expected version {expectedVersion}).")
            : (await GetAsync(schedule.Id, ct))!;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        int rows = await db.Schedules.Where(x => x.Id == id).ExecuteDeleteAsync(ct);
        return rows > 0;
    }

    public async Task TouchLastRunAsync(Guid id, DateTimeOffset at, CancellationToken ct = default)
    {
        await db.Schedules.Where(x => x.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.LastRunAt, at), ct);
    }

    internal static ScheduleEntity ToEntity(ComparisonSchedule s) => new()
    {
        Id = s.Id,
        BranchId = s.BranchId,
        InstanceId = s.InstanceId,
        BranchKey = s.BranchKey,
        InstanceKey = s.InstanceKey,
        Key = s.Key,
        Name = s.Name,
        Cron = s.Cron,
        OptionsJson = DiffPdfJson.Serialize(s.Options),
        GateJson = s.Gate is null ? null : DiffPdfJson.Serialize(s.Gate),
        SearchPattern = s.SearchPattern,
        Recursive = s.Recursive,
        MaxDegreeOfParallelism = s.MaxDegreeOfParallelism,
        Enabled = s.Enabled,
        CreatedAt = s.CreatedAt,
        LastRunAt = s.LastRunAt,
        Version = 1,
    };
}
