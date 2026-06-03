using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DiffPdf.Notifications;

/// <summary>Fans a finished-batch notification out to every matching subscription.</summary>
public interface INotificationDispatcher
{
    Task DispatchAsync(BatchNotification notification, CancellationToken ct = default);
}

/// <summary>
/// Best-effort dispatcher: matches the notification against configured subscriptions
/// (event + optional branch/instance filter) and sends via the channel's <see cref="INotifier"/>.
/// A failure on one subscription is logged and never blocks the others.
/// </summary>
public sealed class NotificationDispatcher(
    IEnumerable<INotifier> notifiers,
    IOptions<NotificationOptions> options,
    ILogger<NotificationDispatcher> logger) : INotificationDispatcher
{
    public async Task DispatchAsync(BatchNotification notification, CancellationToken ct = default)
    {
        var opts = options.Value;
        if (!opts.Enabled)
            return;

        foreach (var subscription in opts.Subscriptions)
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
