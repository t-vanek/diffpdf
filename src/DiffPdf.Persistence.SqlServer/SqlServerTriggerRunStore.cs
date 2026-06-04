using DiffPdf.Core.Models;
using DiffPdf.Persistence.SqlServer.Entities;
using DiffPdf.Persistence.SqlServer.Mapping;
using Microsoft.EntityFrameworkCore;

namespace DiffPdf.Persistence.SqlServer;

/// <summary>EF Core (SQL Server) append-only trigger-run history.</summary>
public sealed class SqlServerTriggerRunStore(DiffPdfDbContext db, EntityMapper mapper) : ITriggerRunStore
{
    public async Task AddAsync(TriggerRun run, CancellationToken ct = default)
    {
        db.TriggerRuns.Add(ToEntity(run));
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<TriggerRun>> ListByTriggerAsync(Guid triggerId, int limit = 50, CancellationToken ct = default)
    {
        var rows = await db.TriggerRuns.AsNoTracking()
            .Where(x => x.TriggerId == triggerId)
            .OrderByDescending(x => x.StartedAt)
            .Take(limit)
            .ToListAsync(ct);
        return rows.Select(mapper.ToDomain).ToList();
    }

    public async Task<TriggerRun?> GetByIdempotencyKeyAsync(Guid triggerId, string idempotencyKey, CancellationToken ct = default)
    {
        var e = await db.TriggerRuns.AsNoTracking()
            .FirstOrDefaultAsync(x => x.TriggerId == triggerId && x.IdempotencyKey == idempotencyKey, ct);
        return e is null ? null : mapper.ToDomain(e);
    }

    private static TriggerRunEntity ToEntity(TriggerRun r) => new()
    {
        Id = r.Id,
        TriggerId = r.TriggerId,
        BatchJobId = r.BatchJobId,
        Source = r.Source.ToString(),
        Status = r.Status,
        Result = r.Result,
        StartedAt = r.StartedAt,
        FinishedAt = r.FinishedAt,
        DurationMs = r.DurationMs,
        Error = r.Error,
        RequestedBy = r.RequestedBy,
        IdempotencyKey = r.IdempotencyKey,
    };
}
