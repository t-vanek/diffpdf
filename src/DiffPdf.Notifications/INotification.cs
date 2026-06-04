using DiffPdf.Core.Models;

namespace DiffPdf.Notifications;

/// <summary>
/// A channel-agnostic notification: an event plus a rendered title/summary and structured detail,
/// ready to be delivered by any <see cref="INotifier"/>. Implemented by both batch-outcome and
/// control-check notifications so they share one dispatch path and one set of channels.
/// </summary>
public interface INotification
{
    /// <summary>The event this notification announces (matched against a subscription's events).</summary>
    NotificationEvent Event { get; }

    /// <summary>Optional branch filter; null = not branch-scoped.</summary>
    string? BranchKey { get; }

    /// <summary>Optional instance filter; null = not instance-scoped.</summary>
    string? InstanceKey { get; }

    /// <summary>One-line headline (e-mail subject / chat title).</summary>
    string Title { get; }

    /// <summary>Human-readable body.</summary>
    string Summary { get; }

    /// <summary>When the event occurred.</summary>
    DateTimeOffset OccurredAt { get; }

    /// <summary>Structured payload serialized under <c>diffpdf</c> in webhook deliveries.</summary>
    object Detail { get; }
}
