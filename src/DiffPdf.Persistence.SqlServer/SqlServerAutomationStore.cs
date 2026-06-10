using DiffPdf.Core.Models;
using DiffPdf.Core.Storage;
using DiffPdf.Persistence.SqlServer.Entities;
using DiffPdf.Persistence.SqlServer.Mapping;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace DiffPdf.Persistence.SqlServer;

/// <summary>
/// EF Core (SQL Server) automation store. The engine-state operations are single conditional
/// <c>ExecuteUpdate</c> statements (atomic without a transaction) on a column set disjoint from
/// <see cref="UpdateAsync"/>'s, so user edits and engine state never clobber each other.
/// </summary>
public sealed class SqlServerAutomationStore(DiffPdfDbContext db, EntityMapper mapper) : IAutomationStore
{
    public async Task<Automation> CreateAsync(Automation automation, CancellationToken ct = default)
    {
        db.Automations.Add(ToEntity(automation));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
        {
            throw new DuplicateKeyException($"Automation '{automation.Key}' already exists.");
        }
        return (await GetAsync(automation.Id, ct))!;
    }

    public async Task<Automation?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var e = await db.Automations.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        return e is null ? null : mapper.ToDomain(e);
    }

    public async Task<Automation?> GetByKeyAsync(string key, CancellationToken ct = default)
    {
        var e = await db.Automations.AsNoTracking().FirstOrDefaultAsync(x => x.Key == key, ct);
        return e is null ? null : mapper.ToDomain(e);
    }

    public async Task<IReadOnlyList<Automation>> ListAsync(CancellationToken ct = default)
    {
        var rows = await db.Automations.AsNoTracking().OrderBy(x => x.Key).ToListAsync(ct);
        return rows.Select(mapper.ToDomain).ToList();
    }

    public async Task<IReadOnlyList<Automation>> ListEnabledAsync(CancellationToken ct = default)
    {
        var rows = await db.Automations.AsNoTracking()
            .Where(x => x.Enabled).OrderBy(x => x.Key).ToListAsync(ct);
        return rows.Select(mapper.ToDomain).ToList();
    }

    public async Task<Automation> UpdateAsync(Automation automation, long expectedVersion, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        string stepsJson = DiffPdfJson.Serialize(automation.Steps);
        string eventTriggersJson = DiffPdfJson.Serialize(automation.EventTriggers);
        string eventsJson = DiffPdfJson.Serialize(automation.Events);

        int rows;
        try
        {
            // ExecuteUpdate runs SQL directly, so a unique-key violation surfaces as the raw
            // SqlException (not wrapped in DbUpdateException like SaveChanges). NextRunAt is the one
            // engine-state column also written here — the service layer recomputes it on cadence edits.
            rows = await db.Automations
                .Where(x => x.Id == automation.Id && x.Version == expectedVersion)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Key, automation.Key)
                    .SetProperty(x => x.Name, automation.Name)
                    .SetProperty(x => x.ScopeKind, automation.ScopeKind.ToString())
                    .SetProperty(x => x.BranchKey, automation.BranchKey)
                    .SetProperty(x => x.InstanceKey, automation.InstanceKey)
                    .SetProperty(x => x.Cron, automation.Cron)
                    .SetProperty(x => x.IntervalSeconds, automation.IntervalSeconds)
                    .SetProperty(x => x.EventTriggersJson, eventTriggersJson)
                    .SetProperty(x => x.EventDebounceSeconds, automation.EventDebounceSeconds)
                    .SetProperty(x => x.StepsJson, stepsJson)
                    .SetProperty(x => x.TimeoutSeconds, automation.TimeoutSeconds)
                    .SetProperty(x => x.MaxAttempts, automation.MaxAttempts)
                    .SetProperty(x => x.RetryDelaySeconds, automation.RetryDelaySeconds)
                    .SetProperty(x => x.FailureThreshold, automation.FailureThreshold)
                    .SetProperty(x => x.EventsJson, eventsJson)
                    .SetProperty(x => x.Enabled, automation.Enabled)
                    .SetProperty(x => x.NextRunAt, automation.NextRunAt)
                    .SetProperty(x => x.UpdatedAt, now)
                    .SetProperty(x => x.Version, x => x.Version + 1), ct);
        }
        catch (SqlException ex) when (ex.Number is 2601 or 2627)
        {
            throw new DuplicateKeyException($"Automation '{automation.Key}' already exists.");
        }

        return rows == 0
            ? throw new ConcurrencyConflictException($"Automation update conflict for {automation.Id} (expected version {expectedVersion}).")
            : (await GetAsync(automation.Id, ct))!;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        int rows = await db.Automations.Where(x => x.Id == id).ExecuteDeleteAsync(ct);
        return rows > 0;
    }

    public async Task<bool> TryBeginRunAsync(Guid id, DateTimeOffset now, DateTimeOffset? advanceNextRunAtTo, TimeSpan staleAfter, CancellationToken ct = default)
    {
        var staleBefore = now - staleAfter;
        int rows = await db.Automations
            .Where(x => x.Id == id && (x.RunningSince == null || x.RunningSince < staleBefore))
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.RunningSince, now)
                .SetProperty(x => x.NextRunAt, x => advanceNextRunAtTo ?? x.NextRunAt), ct);
        return rows == 1;
    }

    public async Task CompleteRunAsync(Guid id, DateTimeOffset at, AutomationRunOutcome outcome, int consecutiveFailures, CancellationToken ct = default)
    {
        await db.Automations.Where(x => x.Id == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.RunningSince, (DateTimeOffset?)null)
                .SetProperty(x => x.LastRunAt, at)
                .SetProperty(x => x.LastOutcome, outcome.ToString())
                .SetProperty(x => x.ConsecutiveFailures, consecutiveFailures), ct);
    }

    public async Task SetNextRunIfNullAsync(Guid id, DateTimeOffset next, CancellationToken ct = default)
    {
        await db.Automations.Where(x => x.Id == id && x.NextRunAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.NextRunAt, next), ct);
    }

    public async Task<bool> TryClaimEventTriggerAsync(Guid id, DateTimeOffset now, TimeSpan debounce, CancellationToken ct = default)
    {
        var claimableBefore = now - debounce;
        int rows = await db.Automations
            .Where(x => x.Id == id && x.Enabled
                        && (x.LastEventTriggeredAt == null || x.LastEventTriggeredAt <= claimableBefore))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.LastEventTriggeredAt, now), ct);
        return rows == 1;
    }

    internal static AutomationEntity ToEntity(Automation a) => new()
    {
        Id = a.Id,
        Key = a.Key,
        Name = a.Name,
        ScopeKind = a.ScopeKind.ToString(),
        BranchKey = a.BranchKey,
        InstanceKey = a.InstanceKey,
        Cron = a.Cron,
        IntervalSeconds = a.IntervalSeconds,
        EventTriggersJson = DiffPdfJson.Serialize(a.EventTriggers),
        EventDebounceSeconds = a.EventDebounceSeconds,
        StepsJson = DiffPdfJson.Serialize(a.Steps),
        TimeoutSeconds = a.TimeoutSeconds,
        MaxAttempts = a.MaxAttempts,
        RetryDelaySeconds = a.RetryDelaySeconds,
        FailureThreshold = a.FailureThreshold,
        EventsJson = DiffPdfJson.Serialize(a.Events),
        Enabled = a.Enabled,
        NextRunAt = a.NextRunAt,
        RunningSince = a.RunningSince,
        ConsecutiveFailures = a.ConsecutiveFailures,
        LastEventTriggeredAt = a.LastEventTriggeredAt,
        CreatedAt = a.CreatedAt,
        LastRunAt = a.LastRunAt,
        LastOutcome = a.LastOutcome?.ToString(),
        Version = 1,
    };
}
