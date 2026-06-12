using DiffPdf.Application.Abstractions;
using DiffPdf.Core.Models;
using DiffPdf.Notifications;
using DiffPdf.Persistence;
using Microsoft.Extensions.Logging;

namespace DiffPdf.Messaging;

/// <summary>
/// One delivery pass over the notification outbox — the testable core of
/// <see cref="NotificationDeliveryService"/> (which adds the leader gate and the timer). Claims the due
/// rows, e-mails them, and records each attempt's outcome: a failure schedules a retry with backoff
/// (1 min → 5 min → 30 min), the <see cref="NotificationDeliveryOptions.MaxAttempts"/>-th failure parks the
/// row as DeadLetter. Missing SMTP configuration counts as a failed attempt, so a misconfigured server
/// surfaces as dead-lettered rows instead of silently dropped mail. Delivery is at-least-once: a row that
/// fails on its 3rd of 5 recipients is retried whole, so earlier recipients may receive a duplicate —
/// preferred over losing the alert.
/// </summary>
public sealed class NotificationDeliveryPump(
    INotificationDeliveryStore store,
    EmailSettingsResolver settingsResolver,
    IEmailSender sender,
    ISystemEventLog systemEvents,
    DiffPdfMetrics metrics,
    NotificationDeliveryOptions options,
    ILogger logger)
{
    /// <summary>Sends the due rows (up to the batch size) and refreshes the backlog gauges. Returns the number processed.</summary>
    public async Task<int> DeliverDueAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        var due = await store.ListDueAsync(now, options.BatchSize, ct);
        if (due.Count > 0)
        {
            var settings = await settingsResolver.ResolveAsync(ct);
            foreach (var delivery in due)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (settings is not { IsConfigured: true })
                        throw new InvalidOperationException("E-mail není nakonfigurován (Konfigurace → E-mail, nebo Notifications:Smtp).");

                    await sender.SendAsync(settings, delivery.Recipients, delivery.Subject, delivery.Body, ct: ct);
                    await store.MarkSentAsync(delivery.Id, DateTimeOffset.UtcNow, CancellationToken.None);
                    metrics.RecordNotificationDelivery("sent");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw; // shutdown — the row stays due and the next leader tick retries it
                }
                catch (Exception ex)
                {
                    int attempts = delivery.AttemptCount + 1;
                    bool dead = attempts >= Math.Max(1, options.MaxAttempts);
                    var next = dead ? (DateTimeOffset?)null : DateTimeOffset.UtcNow + NotificationDeliveryOptions.Backoff(attempts);
                    await store.MarkFailedAsync(delivery.Id, Truncate(ex.Message), attempts, next, dead, CancellationToken.None);
                    metrics.RecordNotificationDelivery(dead ? "deadletter" : "failed");
                    if (dead)
                    {
                        logger.LogError(ex,
                            "E-mail delivery {Id} ({Event}, rule '{Rule}') dead-lettered after {Attempts} attempt(s).",
                            delivery.Id, delivery.Event, delivery.RuleName, attempts);
                        // A dead-lettered alert is itself an alert-worthy fact — surface it in the event feed
                        // so the operator learns about it even though the e-mail channel just proved broken.
                        await systemEvents.AppendAsync(new SystemEvent
                        {
                            Type = SystemEventTypes.NotificationDeadLetter,
                            Severity = SystemEventSeverity.Error,
                            BranchKey = delivery.BranchKey,
                            InstanceKey = delivery.InstanceKey,
                            Message = $"E-mailovou notifikaci „{delivery.Subject}“ se nepodařilo doručit ({attempts} pokusů) — viz Konfigurace → E-mail.",
                            Detail = $"Pravidlo „{delivery.RuleName}“, příjemci: {string.Join(", ", delivery.Recipients)}. {ex.Message}",
                        }, CancellationToken.None);
                    }
                    else
                        logger.LogWarning(ex,
                            "E-mail delivery {Id} ({Event}, rule '{Rule}') failed attempt {Attempts}/{Max}; retry at {Next:HH:mm:ss}.",
                            delivery.Id, delivery.Event, delivery.RuleName, attempts, options.MaxAttempts, next);
                }
            }
        }

        // Refresh the backlog gauges every pass so /metrics and the UI badge stay current.
        metrics.RecordNotificationBacklog(
            deadLetter: await store.CountAsync(NotificationDeliveryStatus.DeadLetter, ct),
            pending: await store.CountAsync(NotificationDeliveryStatus.Pending, ct)
                     + await store.CountAsync(NotificationDeliveryStatus.Failed, ct));
        return due.Count;
    }

    private static string Truncate(string message) =>
        message.Length <= 1000 ? message : message[..1000] + "…";
}
