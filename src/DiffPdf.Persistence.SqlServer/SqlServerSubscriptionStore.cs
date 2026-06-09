using DiffPdf.Core.Models;
using DiffPdf.Core.Storage;
using DiffPdf.Persistence.SqlServer.Entities;
using DiffPdf.Persistence.SqlServer.Mapping;
using Microsoft.EntityFrameworkCore;

namespace DiffPdf.Persistence.SqlServer;

/// <summary>EF Core (SQL Server) notification-subscription store — runtime-managed delivery rules.</summary>
public sealed class SqlServerSubscriptionStore(DiffPdfDbContext db, EntityMapper mapper) : ISubscriptionStore
{
    public async Task<NotificationSubscription> CreateAsync(NotificationSubscription subscription, CancellationToken ct = default)
    {
        db.Subscriptions.Add(ToEntity(subscription));
        await db.SaveChangesAsync(ct);
        return (await GetAsync(subscription.Id, ct))!;
    }

    public async Task<NotificationSubscription?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var e = await db.Subscriptions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        return e is null ? null : mapper.ToDomain(e);
    }

    public async Task<IReadOnlyList<NotificationSubscription>> ListAsync(CancellationToken ct = default)
    {
        var rows = await db.Subscriptions.AsNoTracking().OrderBy(x => x.CreatedAt).ToListAsync(ct);
        return rows.Select(mapper.ToDomain).ToList();
    }

    public async Task<IReadOnlyList<NotificationSubscription>> ListEnabledAsync(CancellationToken ct = default)
    {
        var rows = await db.Subscriptions.AsNoTracking()
            .Where(x => x.Enabled).OrderBy(x => x.CreatedAt).ToListAsync(ct);
        return rows.Select(mapper.ToDomain).ToList();
    }

    public async Task<NotificationSubscription> UpdateAsync(NotificationSubscription subscription, long expectedVersion, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        string recipientsJson = DiffPdfJson.Serialize(subscription.Recipients);
        string eventsJson = DiffPdfJson.Serialize(subscription.Events);
        string branchKeysJson = DiffPdfJson.Serialize(subscription.BranchKeys);
        string instanceKeysJson = DiffPdfJson.Serialize(subscription.InstanceKeys);

        int rows = await db.Subscriptions
            .Where(x => x.Id == subscription.Id && x.Version == expectedVersion)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Name, subscription.Name)
                .SetProperty(x => x.RecipientsJson, recipientsJson)
                .SetProperty(x => x.EventsJson, eventsJson)
                .SetProperty(x => x.BranchKeysJson, branchKeysJson)
                .SetProperty(x => x.InstanceKeysJson, instanceKeysJson)
                .SetProperty(x => x.Enabled, subscription.Enabled)
                .SetProperty(x => x.UpdatedAt, now)
                .SetProperty(x => x.Version, x => x.Version + 1), ct);

        return rows == 0
            ? throw new ConcurrencyConflictException($"Subscription update conflict for {subscription.Id} (expected version {expectedVersion}).")
            : (await GetAsync(subscription.Id, ct))!;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        int rows = await db.Subscriptions.Where(x => x.Id == id).ExecuteDeleteAsync(ct);
        return rows > 0;
    }

    internal static SubscriptionEntity ToEntity(NotificationSubscription s) => new()
    {
        Id = s.Id,
        Name = s.Name,
        RecipientsJson = DiffPdfJson.Serialize(s.Recipients),
        EventsJson = DiffPdfJson.Serialize(s.Events),
        BranchKeysJson = DiffPdfJson.Serialize(s.BranchKeys),
        InstanceKeysJson = DiffPdfJson.Serialize(s.InstanceKeys),
        Enabled = s.Enabled,
        CreatedAt = s.CreatedAt,
        Version = 1,
    };
}
