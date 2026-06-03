using DiffPdf.Core.Models;
using DiffPdf.Persistence;
using Microsoft.Extensions.Logging;

namespace DiffPdf.Notifications;

/// <summary>Fans a finished-batch notification out to every matching subscription.</summary>
public interface INotificationDispatcher
{
    Task DispatchAsync(BatchNotification notification, CancellationToken ct = default);
}

/// <summary>
/// Best-effort dispatcher: reads the enabled subscriptions from the store, matches each
/// against the notification (event + optional branch/instance filter) and sends via the
/// channel's <see cref="INotifier"/>. A failure on one subscription is logged and never
/// blocks the others. Registered Scoped so its scoped <see cref="ISubscriptionStore"/>
/// resolves in the same per-message DI scope as the Wolverine handler that invokes it.
/// </summary>
public sealed class NotificationDispatcher(
    IEnumerable<INotifier> notifiers,
    ISubscriptionStore subscriptions,
    ILogger<NotificationDispatcher> logger) : INotificationDispatcher
{
    public async Task DispatchAsync(BatchNotification notification, CancellationToken ct = default)
    {
        var subs = await subscriptions.ListEnabledAsync(ct);

        foreach (var subscription in subs)
        {
            if (!subscription.Events.Contains(notification.Event))
                continue;
            if (!string.IsNullOrWhiteSpace(subscription.BranchKey)
                && !string.Equals(subscription.BranchKey, notification.BranchKey, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!string.IsNullOrWhiteSpace(subscription.InstanceKey)
                && !string.Equals(subscription.InstanceKey, notification.InstanceKey, StringComparison.OrdinalIgnoreCase))
                continue;

            var notifier = notifiers.FirstOrDefault(n =>
                string.Equals(n.Channel, subscription.Channel, StringComparison.OrdinalIgnoreCase));
            if (notifier is null)
            {
                logger.LogWarning("No notifier for channel '{Channel}'; skipping subscription to {Target}.",
                    subscription.Channel, subscription.Target);
                continue;
            }

            try
            {
                await notifier.SendAsync(subscription, notification, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Notification via {Channel} to {Target} failed for job {JobId}.",
                    subscription.Channel, subscription.Target, notification.JobId);
            }
        }
    }
}
