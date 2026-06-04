using DiffPdf.Core.Models;
using DiffPdf.Persistence.Postgres.Entities;
using DiffPdf.Persistence.Postgres.Mapping;
using Microsoft.EntityFrameworkCore;

namespace DiffPdf.Persistence.Postgres;

/// <summary>EF Core (PostgreSQL) append-only trigger-run history.</summary>
public sealed class PostgresTriggerRunStore(DiffPdfDbContext db, EntityMapper mapper) : ITriggerRunStore
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

    public Task<int> DeleteStartedBeforeAsync(DateTimeOffset startedBefore, CancellationToken ct = default) =>
        db.TriggerRuns.Where(x => x.StartedAt < startedBefore).ExecuteDeleteAsync(ct);

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
