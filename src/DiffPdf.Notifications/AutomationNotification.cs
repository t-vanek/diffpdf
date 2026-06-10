using DiffPdf.Core.Models;

namespace DiffPdf.Notifications;

/// <summary>
/// A channel-agnostic description of an automation run result, ready to be rendered into an e-mail.
/// Raised for the automation's configured failure events, the recovery event and the consecutive-failure
/// escalation (<see cref="NotificationEvent.AutomationFailing"/>).
/// </summary>
public sealed record AutomationNotification(
    NotificationEvent Event,
    string AutomationKey,
    string AutomationName,
    string? BranchKey,
    string? InstanceKey,
    AutomationRunOutcome Outcome,
    string DetailText,
    DateTimeOffset OccurredAt) : INotification
{
    /// <summary>One-line headline suitable for an e-mail subject or chat title.</summary>
    public string Title => Event switch
    {
        NotificationEvent.AutomationRecovered => $"diffpdf — automation recovered: {AutomationKey}",
        NotificationEvent.AutomationFailing => $"diffpdf — automation failing: {AutomationKey}",
        _ => $"diffpdf — automation {Outcome.ToString().ToUpperInvariant()}: {AutomationKey}",
    };

    /// <summary>Human-readable body with the scope and the run's detail.</summary>
    public string Summary
    {
        get
        {
            var scope = (BranchKey, InstanceKey) switch
            {
                (null, _) => "scope: global",
                (var b, null) => $"scope: branch {b}",
                var (b, i) => $"scope: {b}/{i}",
            };
            return string.Join('\n',
                $"Automation '{AutomationName}' — {Outcome}.",
                scope,
                DetailText,
                $"At {OccurredAt:u}.");
        }
    }

    /// <summary>The whole automation notification is the structured detail.</summary>
    public object Detail => this;
}
