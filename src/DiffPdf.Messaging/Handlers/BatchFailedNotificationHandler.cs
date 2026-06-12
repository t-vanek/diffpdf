using DiffPdf.Application.Abstractions;
using DiffPdf.Core.Models;
using DiffPdf.Messaging.Messages;
using DiffPdf.Notifications;
using Microsoft.Extensions.Options;

namespace DiffPdf.Messaging.Handlers;

/// <summary>
/// Turns a <see cref="BatchFailed"/> event into an outbound <c>Failed</c> notification and fires
/// event-triggered automations for the failure.
/// </summary>
public sealed class BatchFailedNotificationHandler
{
    public static async Task Handle(
        BatchFailed evt,
        INotificationDispatcher dispatcher,
        IAutomationEventSink automationEvents,
        IOptions<NotificationOptions> options,
        CancellationToken ct)
    {
        var notification = new BatchNotification(
            NotificationEvent.Failed, evt.JobId, evt.BranchKey, evt.InstanceKey,
            Total: 0, Identical: 0, Differing: 0, Errors: 0, FilesWithContentErrors: 0,
            Passed: false, GateViolations: [], OccurredAt: evt.OccurredAt, Link: options.Value.JobLink(evt.JobId));

        await dispatcher.DispatchAsync(notification, ct);

        // See BatchFinishedNotificationHandler: batch outcomes fire event-triggered automations, with the
        // launching automation excluded so a failing scheduled/re-run batch can never re-trigger its own
        // launcher in a loop.
        await automationEvents.PublishAsync(
            NotificationEvent.Failed, evt.BranchKey, evt.InstanceKey,
            $"{evt.BranchKey}/{evt.InstanceKey}: {evt.Error}",
            sourceAutomationId: evt.SourceAutomationId,
            chainDepth: evt.SourceAutomationId is null ? 0 : 1, ct);
    }
}
