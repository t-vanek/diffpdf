using DiffPdf.Core.Models;
using DiffPdf.Persistence.SqlServer.Entities;
using DiffPdf.Persistence.SqlServer.Mapping;
using Microsoft.EntityFrameworkCore;

namespace DiffPdf.Persistence.SqlServer;

/// <summary>EF Core (SQL Server) automation-run history store (one row per attempt).</summary>
public sealed class SqlServerAutomationRunStore(DiffPdfDbContext db, EntityMapper mapper) : IAutomationRunStore
{
    public async Task AddAsync(AutomationRun run, CancellationToken ct = default)
    {
        db.AutomationRuns.Add(new AutomationRunEntity
        {
            Id = run.Id,
            AutomationId = run.AutomationId,
            TriggerKind = run.Trigger.ToString(),
            TriggerDetail = run.TriggerDetail,
            Attempt = run.Attempt,
            StartedAt = run.StartedAt,
            CompletedAt = run.CompletedAt,
            Outcome = run.Outcome.ToString(),
            Detail = run.Detail,
            StepResultsJson = DiffPdfJson.Serialize(run.StepResults),
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<AutomationRun>> ListByAutomationAsync(Guid automationId, int limit = 50, CancellationToken ct = default)
    {
        var rows = await db.AutomationRuns.AsNoTracking()
            .Where(x => x.AutomationId == automationId)
            .OrderByDescending(x => x.StartedAt).ThenByDescending(x => x.Attempt)
            .Take(limit)
            .ToListAsync(ct);
        return rows.Select(mapper.ToDomain).ToList();
    }

    public async Task<int> DeleteStartedBeforeAsync(DateTimeOffset startedBefore, CancellationToken ct = default) =>
        await db.AutomationRuns.Where(x => x.StartedAt < startedBefore).ExecuteDeleteAsync(ct);
}
