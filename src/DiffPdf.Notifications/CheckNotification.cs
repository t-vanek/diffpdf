using DiffPdf.Core.Models;

namespace DiffPdf.Notifications;

/// <summary>
/// A channel-agnostic description of a control-check result, ready to be rendered into an e-mail,
/// a Slack/Teams message or a generic webhook payload.
/// </summary>
public sealed record CheckNotification(
    NotificationEvent Event,
    string CheckKey,
    string CheckName,
    CheckType CheckType,
    string? BranchKey,
    string? InstanceKey,
    CheckRunOutcome Outcome,
    string DetailText,
    DateTimeOffset OccurredAt) : INotification
{
    /// <summary>One-line headline suitable for an e-mail subject or chat title.</summary>
    public string Title => Event == NotificationEvent.CheckRecovered
        ? $"diffpdf — check recovered: {CheckKey}"
        : $"diffpdf — check {Outcome.ToString().ToUpperInvariant()}: {CheckKey}";

    /// <summary>Human-readable body with the scope and the check's detail.</summary>
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
                $"Check '{CheckName}' ({CheckType}) — {Outcome}.",
                scope,
                DetailText,
                $"At {OccurredAt:u}.");
        }
    }

    /// <summary>The whole check notification is the structured webhook detail.</summary>
    public object Detail => this;
}
