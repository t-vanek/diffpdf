using DiffPdf.Core.Models;
using DiffPdf.Persistence.Postgres.Entities;
using DiffPdf.Persistence.Postgres.Mapping;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DiffPdf.Persistence.Postgres;

/// <summary>EF Core (PostgreSQL) schedule-run history store.</summary>
public sealed class PostgresScheduleRunStore(DiffPdfDbContext db, EntityMapper mapper) : IScheduleRunStore
{
    public async Task RecordStartAsync(Guid scheduleId, Guid jobId, DateTimeOffset startedAt, CancellationToken ct = default)
    {
        db.ScheduleRuns.Add(new ScheduleRunEntity
        {
            Id = Guid.NewGuid(),
            ScheduleId = scheduleId,
            JobId = jobId,
            StartedAt = startedAt,
            Outcome = nameof(ScheduleRunOutcome.Pending),
        });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Idempotent: a run for this job is already recorded.
        }
    }

    public async Task CompleteByJobAsync(
        Guid jobId, ScheduleRunOutcome outcome, int differing, int errors, int filesWithContentErrors,
        bool passed, IReadOnlyList<string> gateViolations, string? error, DateTimeOffset completedAt,
        CancellationToken ct = default)
    {
        string? gvJson = gateViolations.Count == 0 ? null : DiffPdfJson.Serialize(gateViolations);

        // No-op (0 rows) when the job was not launched by a schedule.
        await db.ScheduleRuns
            .Where(x => x.JobId == jobId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Outcome, outcome.ToString())
                .SetProperty(x => x.Differing, differing)
                .SetProperty(x => x.Errors, errors)
                .SetProperty(x => x.FilesWithContentErrors, filesWithContentErrors)
                .SetProperty(x => x.Passed, passed)
                .SetProperty(x => x.GateViolationsJson, gvJson)
                .SetProperty(x => x.Error, error)
                .SetProperty(x => x.CompletedAt, completedAt), ct);
    }

    public async Task<IReadOnlyList<ScheduleRun>> ListByScheduleAsync(Guid scheduleId, int limit = 50, CancellationToken ct = default)
    {
        var rows = await db.ScheduleRuns.AsNoTracking()
            .Where(x => x.ScheduleId == scheduleId)
            .OrderByDescending(x => x.StartedAt)
            .Take(limit)
            .ToListAsync(ct);
        return rows.Select(mapper.ToDomain).ToList();
    }
}
