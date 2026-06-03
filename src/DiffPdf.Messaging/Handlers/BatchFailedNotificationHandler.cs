using DiffPdf.Core.Models;
using DiffPdf.Messaging.Messages;
using DiffPdf.Notifications;

namespace DiffPdf.Messaging.Handlers;

/// <summary>Turns a <see cref="BatchFailed"/> event into an outbound <c>Failed</c> notification.</summary>
public sealed class BatchFailedNotificationHandler
{
    public static Task Handle(BatchFailed evt, INotificationDispatcher dispatcher, CancellationToken ct)
    {
        var notification = new BatchNotification(
            NotificationEvent.Failed, evt.JobId, evt.BranchKey, evt.InstanceKey,
            Total: 0, Identical: 0, Differing: 0, Errors: 0, FilesWithContentErrors: 0,
            Passed: false, GateViolations: [], OccurredAt: evt.OccurredAt);

        return dispatcher.DispatchAsync(notification, ct);
    }
}
