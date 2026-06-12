using DiffPdf.Core.Models;
using DiffPdf.Persistence.SqlServer.Entities;
using DiffPdf.Persistence.SqlServer.Mapping;
using Microsoft.EntityFrameworkCore;

namespace DiffPdf.Persistence.SqlServer;

/// <summary>EF Core (SQL Server) system event log store. Seq is a bigint identity — the replay cursor.</summary>
public sealed class SqlServerSystemEventStore(DiffPdfDbContext db, EntityMapper mapper) : ISystemEventStore
{
    public async Task<SystemEvent> AppendAsync(SystemEvent evt, CancellationToken ct = default)
    {
        var entity = new SystemEventEntity
        {
            Type = evt.Type,
            Severity = evt.Severity.ToString(),
            BranchKey = evt.BranchKey,
            InstanceKey = evt.InstanceKey,
            JobId = evt.JobId,
            AutomationId = evt.AutomationId,
            Message = evt.Message,
            Detail = evt.Detail,
            OccurredAt = evt.OccurredAt,
        };
        db.SystemEvents.Add(entity);
        await db.SaveChangesAsync(ct);
        return evt with { Seq = entity.Seq };
    }

    public async Task<IReadOnlyList<SystemEvent>> ListSinceAsync(long sinceSeq, int limit, SystemEventSeverity? minSeverity = null, CancellationToken ct = default)
    {
        // Severity is stored as a name; translate the "at least" filter into the matching name set.
        string[]? allowed = minSeverity is { } min
            ? Enum.GetValues<SystemEventSeverity>().Where(s => s >= min).Select(s => s.ToString()).ToArray()
            : null;
        var rows = await db.SystemEvents.AsNoTracking()
            .Where(x => x.Seq > sinceSeq && (allowed == null || allowed.Contains(x.Severity)))
            .OrderBy(x => x.Seq)
            .Take(limit)
            .ToListAsync(ct);
        return rows.Select(mapper.ToDomain).ToList();
    }

    public async Task<IReadOnlyList<SystemEvent>> ListRecentAsync(int limit, CancellationToken ct = default)
    {
        var rows = await db.SystemEvents.AsNoTracking()
            .OrderByDescending(x => x.Seq)
            .Take(limit)
            .ToListAsync(ct);
        return rows.Select(mapper.ToDomain).ToList();
    }

    public Task<bool> ExistsForJobAsync(string type, Guid jobId, DateTimeOffset after, CancellationToken ct = default) =>
        db.SystemEvents.AsNoTracking().AnyAsync(x => x.JobId == jobId && x.Type == type && x.OccurredAt > after, ct);

    public Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default) =>
        db.SystemEvents.Where(x => x.OccurredAt < cutoff).ExecuteDeleteAsync(ct);
}
