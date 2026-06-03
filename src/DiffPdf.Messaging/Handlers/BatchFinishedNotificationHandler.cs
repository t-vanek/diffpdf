using DiffPdf.Messaging.Messages;
using DiffPdf.Notifications;

namespace DiffPdf.Messaging.Handlers;

/// <summary>Turns a <see cref="BatchFinished"/> event into outbound notifications.</summary>
public sealed class BatchFinishedNotificationHandler
{
    public static Task Handle(BatchFinished evt, INotificationDispatcher dispatcher, CancellationToken ct)
    {
        var kind = evt.Passed ? NotificationEvent.Completed : NotificationEvent.GateViolated;
        var notification = new BatchNotification(
            kind, evt.JobId, evt.BranchKey, evt.InstanceKey,
            evt.Total, evt.Identical, evt.Differing, evt.Errors, evt.FilesWithContentErrors,
            evt.Passed, evt.GateViolations, evt.CompletedAt);

        return dispatcher.DispatchAsync(notification, ct);
    }
}
