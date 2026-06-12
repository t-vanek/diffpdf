using DiffPdf.Core.Models;
using DiffPdf.Persistence.SqlServer.Entities;
using DiffPdf.Persistence.SqlServer.Mapping;
using Microsoft.EntityFrameworkCore;

namespace DiffPdf.Persistence.SqlServer;

/// <summary>EF Core (SQL Server) notification outbox store.</summary>
public sealed class SqlServerNotificationDeliveryStore(DiffPdfDbContext db, EntityMapper mapper) : INotificationDeliveryStore
{
    public async Task AddRangeAsync(IReadOnlyList<NotificationDelivery> deliveries, CancellationToken ct = default)
    {
        db.NotificationDeliveries.AddRange(deliveries.Select(ToEntity));
        await db.SaveChangesAsync(ct);
    }

    public async Task<NotificationDelivery?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var e = await db.NotificationDeliveries.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        return e is null ? null : mapper.ToDomain(e);
    }

    public async Task<IReadOnlyList<NotificationDelivery>> ListDueAsync(DateTimeOffset now, int limit, CancellationToken ct = default)
    {
        var rows = await db.NotificationDeliveries.AsNoTracking()
            .Where(x => (x.Status == "Pending" || x.Status == "Failed") && x.NextAttemptAt != null && x.NextAttemptAt <= now)
            .OrderBy(x => x.NextAttemptAt)
            .Take(limit)
            .ToListAsync(ct);
        return rows.Select(mapper.ToDomain).ToList();
    }

    public async Task<IReadOnlyList<NotificationDelivery>> ListRecentAsync(int limit, NotificationDeliveryStatus? status = null, CancellationToken ct = default)
    {
        string? statusName = status?.ToString();
        var rows = await db.NotificationDeliveries.AsNoTracking()
            .Where(x => statusName == null || x.Status == statusName)
            .OrderByDescending(x => x.CreatedAt)
            .Take(limit)
            .ToListAsync(ct);
        return rows.Select(mapper.ToDomain).ToList();
    }

    public Task MarkSentAsync(Guid id, DateTimeOffset sentAt, CancellationToken ct = default) =>
        db.NotificationDeliveries.Where(x => x.Id == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, nameof(NotificationDeliveryStatus.Sent))
                .SetProperty(x => x.SentAt, sentAt)
                .SetProperty(x => x.NextAttemptAt, (DateTimeOffset?)null)
                .SetProperty(x => x.LastError, (string?)null), ct);

    public Task MarkFailedAsync(Guid id, string error, int attemptCount, DateTimeOffset? nextAttemptAt, bool deadLetter, CancellationToken ct = default) =>
        db.NotificationDeliveries.Where(x => x.Id == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, deadLetter ? nameof(NotificationDeliveryStatus.DeadLetter) : nameof(NotificationDeliveryStatus.Failed))
                .SetProperty(x => x.AttemptCount, attemptCount)
                .SetProperty(x => x.NextAttemptAt, deadLetter ? null : nextAttemptAt)
                .SetProperty(x => x.LastError, error), ct);

    public Task<int> CountAsync(NotificationDeliveryStatus status, CancellationToken ct = default)
    {
        string statusName = status.ToString();
        return db.NotificationDeliveries.AsNoTracking().CountAsync(x => x.Status == statusName, ct);
    }

    public async Task<bool> RequeueAsync(Guid id, DateTimeOffset now, CancellationToken ct = default)
    {
        int updated = await db.NotificationDeliveries.Where(x => x.Id == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, nameof(NotificationDeliveryStatus.Pending))
                .SetProperty(x => x.AttemptCount, 0)
                .SetProperty(x => x.NextAttemptAt, now)
                .SetProperty(x => x.LastError, (string?)null), ct);
        return updated > 0;
    }

    public Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default) =>
        db.NotificationDeliveries
            .Where(x => (x.Status == "Sent" || x.Status == "DeadLetter") && x.CreatedAt < cutoff)
            .ExecuteDeleteAsync(ct);

    private static NotificationDeliveryEntity ToEntity(NotificationDelivery d) => new()
    {
        Id = d.Id,
        Event = d.Event.ToString(),
        BranchKey = d.BranchKey,
        InstanceKey = d.InstanceKey,
        SubscriptionId = d.SubscriptionId,
        RuleName = d.RuleName,
        RecipientsJson = DiffPdfJson.Serialize(d.Recipients),
        Subject = d.Subject,
        Body = d.Body,
        Status = d.Status.ToString(),
        AttemptCount = d.AttemptCount,
        NextAttemptAt = d.NextAttemptAt,
        LastError = d.LastError,
        CreatedAt = d.CreatedAt,
        SentAt = d.SentAt,
    };
}
