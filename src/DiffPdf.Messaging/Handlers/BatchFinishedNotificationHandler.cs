using DiffPdf.Core.Models;
using DiffPdf.Messaging.Messages;
using DiffPdf.Notifications;
using Microsoft.Extensions.Options;

namespace DiffPdf.Messaging.Handlers;

/// <summary>Turns a <see cref="BatchFinished"/> event into outbound notifications.</summary>
public sealed class BatchFinishedNotificationHandler
{
    public static Task Handle(BatchFinished evt, INotificationDispatcher dispatcher, IOptions<NotificationOptions> options, CancellationToken ct)
    {
        var kind = evt.Passed ? NotificationEvent.Completed : NotificationEvent.GateViolated;
        var notification = new BatchNotification(
            kind, evt.JobId, evt.BranchKey, evt.InstanceKey,
            evt.Total, evt.Identical, evt.Differing, evt.Errors, evt.FilesWithContentErrors,
            evt.Passed, evt.GateViolations, evt.CompletedAt, options.Value.JobLink(evt.JobId));

        return dispatcher.DispatchAsync(notification, ct);
    }
}
