using DiffPdf.Application.Abstractions;
using DiffPdf.Core.Models;
using DiffPdf.Messaging.Messages;
using DiffPdf.Notifications;
using Microsoft.Extensions.Options;

namespace DiffPdf.Messaging.Handlers;

/// <summary>
/// Turns a <see cref="BatchFinished"/> event into outbound notifications and fires event-triggered
/// automations for the batch outcome.
/// </summary>
public sealed class BatchFinishedNotificationHandler
{
    public static async Task Handle(
        BatchFinished evt,
        INotificationDispatcher dispatcher,
        IAutomationEventSink automationEvents,
        IOptions<NotificationOptions> options,
        CancellationToken ct)
    {
        // A batch that "passed" (gate ok / no gate) but still has errored pairs would otherwise be announced as a
        // plain Completed — the errors silent. Escalate it to a distinct event so failure subscribers hear it.
        var kind = !evt.Passed ? NotificationEvent.GateViolated
            : evt.Errors > 0 ? NotificationEvent.CompletedWithErrors
            : NotificationEvent.Completed;
        var notification = new BatchNotification(
            kind, evt.JobId, evt.BranchKey, evt.InstanceKey,
            evt.Total, evt.Identical, evt.Differing, evt.Errors, evt.FilesWithContentErrors,
            evt.Passed, evt.GateViolations, evt.CompletedAt, options.Value.JobLink(evt.JobId));

        await dispatcher.DispatchAsync(notification, ct);

        // Batch outcomes also fire event-triggered automations — the editor offers Completed /
        // CompletedWithErrors / GateViolated / Failed as spouštěče, so they must actually fire for real
        // batches, not only for automation-raised events. SourceAutomationId excludes the automation whose
        // step launched this batch (its own outcome can never re-trigger it — loop guard); chain depth 1
        // marks automation-launched batches as one hop into a cascade. The sink logs and swallows failures.
        await automationEvents.PublishAsync(
            kind, evt.BranchKey, evt.InstanceKey,
            $"{evt.BranchKey}/{evt.InstanceKey}: {evt.Differing} odlišných, {evt.Errors} chyb z {evt.Total} párů",
            sourceAutomationId: evt.SourceAutomationId,
            chainDepth: evt.SourceAutomationId is null ? 0 : 1, ct);
    }
}
