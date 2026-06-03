using DiffPdf.Core.Models;
using DiffPdf.Persistence.SqlServer.Entities;
using DiffPdf.Persistence.SqlServer.Mapping;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace DiffPdf.Persistence.SqlServer;

/// <summary>EF Core (SQL Server) schedule-run history store.</summary>
public sealed class SqlServerScheduleRunStore(DiffPdfDbContext db, EntityMapper mapper) : IScheduleRunStore
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
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
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
