using DiffPdf.Core.Models;
using DiffPdf.Persistence.SqlServer.Entities;
using DiffPdf.Persistence.SqlServer.Mapping;
using Microsoft.EntityFrameworkCore;

namespace DiffPdf.Persistence.SqlServer;

/// <summary>EF Core (SQL Server) control-check-run history store.</summary>
public sealed class SqlServerControlCheckRunStore(DiffPdfDbContext db, EntityMapper mapper) : IControlCheckRunStore
{
    public async Task AddAsync(ControlCheckRun run, CancellationToken ct = default)
    {
        db.ControlCheckRuns.Add(new ControlCheckRunEntity
        {
            Id = run.Id,
            CheckId = run.CheckId,
            StartedAt = run.StartedAt,
            CompletedAt = run.CompletedAt,
            Outcome = run.Outcome.ToString(),
            Detail = run.Detail,
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<ControlCheckRun>> ListByCheckAsync(Guid checkId, int limit = 50, CancellationToken ct = default)
    {
        var rows = await db.ControlCheckRuns.AsNoTracking()
            .Where(x => x.CheckId == checkId)
            .OrderByDescending(x => x.StartedAt)
            .Take(limit)
            .ToListAsync(ct);
        return rows.Select(mapper.ToDomain).ToList();
    }
}
