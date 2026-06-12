using DiffPdf.Core.Models;
using DiffPdf.Persistence;
using Microsoft.Extensions.Logging;

namespace DiffPdf.Notifications;

/// <summary>Fans a notification (batch outcome or control-check result) out to every matching e-mail rule.</summary>
public interface INotificationDispatcher
{
    Task DispatchAsync(INotification notification, CancellationToken ct = default);
}

/// <summary>
/// Outbox dispatcher: reads the enabled rules, keeps those whose <see cref="NotificationSubscription.Events"/>
/// include the event and whose branch/instance filters match (empty filter = any), then APPENDS one
/// <see cref="NotificationDelivery"/> row per matching rule instead of sending inline. The background
/// <c>NotificationDeliveryService</c> sends with retries/backoff and parks exhausted rows as DeadLetter —
/// so an SMTP outage or missing configuration can no longer silently swallow an alert (the old inline
/// dispatcher logged a warning and dropped the mail). Registered Scoped so its scoped stores resolve in the
/// same per-message DI scope as the Wolverine handler that invokes it.
/// </summary>
public sealed class NotificationDispatcher(
    ISubscriptionStore subscriptions,
    INotificationDeliveryStore deliveries,
    ILogger<NotificationDispatcher> logger) : INotificationDispatcher
{
    public async Task DispatchAsync(INotification notification, CancellationToken ct = default)
    {
        var rules = (await subscriptions.ListEnabledAsync(ct)).Where(r => Matches(r, notification)).ToList();
        if (rules.Count == 0)
            return;

        var now = DateTimeOffset.UtcNow;
        var rows = rules.Select(rule => new NotificationDelivery
        {
            Id = Guid.NewGuid(),
            Event = notification.Event,
            BranchKey = notification.BranchKey,
            InstanceKey = notification.InstanceKey,
            SubscriptionId = rule.Id,
            RuleName = rule.Name,
            Recipients = rule.Recipients,
            Subject = notification.Title,
            Body = notification.Summary,
            Status = NotificationDeliveryStatus.Pending,
            NextAttemptAt = now,
            CreatedAt = now,
        }).ToList();

        await deliveries.AddRangeAsync(rows, ct);
        logger.LogInformation("Queued {Count} e-mail delivery(ies) for {Event}.", rows.Count, notification.Event);
    }

    /// <summary>A rule matches when it lists the event and its branch/instance filters allow it (empty = any).</summary>
    private static bool Matches(NotificationSubscription rule, INotification n) =>
        rule.Events.Contains(n.Event)
        && (rule.BranchKeys.Count == 0
            || rule.BranchKeys.Any(b => string.Equals(b, n.BranchKey, StringComparison.OrdinalIgnoreCase)))
        && (rule.InstanceKeys.Count == 0
            || rule.InstanceKeys.Any(i => string.Equals(i, n.InstanceKey, StringComparison.OrdinalIgnoreCase)));
}
