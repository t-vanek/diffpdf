using DiffPdf.Core.Models;
using DiffPdf.Persistence;
using Microsoft.Extensions.Logging;

namespace DiffPdf.Notifications;

/// <summary>Fans a notification (batch outcome or control-check result) out to every matching e-mail rule.</summary>
public interface INotificationDispatcher
{
    Task DispatchAsync(INotification notification, CancellationToken ct = default);
}

/// <summary>
/// Best-effort dispatcher: reads the enabled rules, keeps those whose <see cref="NotificationSubscription.Events"/>
/// include the event and whose branch/instance filters match (empty filter = any), then e-mails each rule's
/// recipients via <see cref="IEmailSender"/>. A failure on one rule is logged and never blocks the others.
/// Registered Scoped so its scoped <see cref="ISubscriptionStore"/> resolves in the same per-message DI scope as
/// the Wolverine handler that invokes it.
/// </summary>
public sealed class NotificationDispatcher(
    ISubscriptionStore subscriptions,
    EmailSettingsResolver settingsResolver,
    IEmailSender sender,
    ILogger<NotificationDispatcher> logger) : INotificationDispatcher
{
    public async Task DispatchAsync(INotification notification, CancellationToken ct = default)
    {
        var rules = (await subscriptions.ListEnabledAsync(ct)).Where(r => Matches(r, notification)).ToList();
        if (rules.Count == 0)
            return;

        var settings = await settingsResolver.ResolveAsync(ct);
        if (settings is not { IsConfigured: true })
        {
            logger.LogWarning(
                "E-mail is not configured (Konfigurace → E-mail, or Notifications:Smtp); skipping {Count} matching rule(s) for {Event}.",
                rules.Count, notification.Event);
            return;
        }

        foreach (var rule in rules)
        {
            try
            {
                await sender.SendAsync(settings, rule.Recipients, notification.Title, notification.Summary, ct: ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "E-mail notification failed for event {Event} (rule '{Rule}', recipients: {Recipients}).",
                    notification.Event, rule.Name, string.Join(", ", rule.Recipients));
            }
        }
    }

    /// <summary>A rule matches when it lists the event and its branch/instance filters allow it (empty = any).</summary>
    private static bool Matches(NotificationSubscription rule, INotification n) =>
        rule.Events.Contains(n.Event)
        && (rule.BranchKeys.Count == 0
            || rule.BranchKeys.Any(b => string.Equals(b, n.BranchKey, StringComparison.OrdinalIgnoreCase)))
        && (rule.InstanceKeys.Count == 0
            || rule.InstanceKeys.Any(i => string.Equals(i, n.InstanceKey, StringComparison.OrdinalIgnoreCase)));
}
