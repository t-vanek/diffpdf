using DiffPdf.Core.Models;

namespace DiffPdf.Notifications;

/// <summary>Delivers a notification over one concrete channel (webhook, e-mail, …).</summary>
public interface INotifier
{
    /// <summary>Channel name matched against <see cref="NotificationSubscription.Channel"/> (case-insensitive).</summary>
    string Channel { get; }

    /// <summary>Sends the notification to the subscription's target. Should throw only on transient failures.</summary>
    Task SendAsync(NotificationSubscription subscription, BatchNotification notification, CancellationToken ct);
}
