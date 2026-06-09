using DiffPdf.Core.Models;

namespace DiffPdf.Notifications;

/// <summary>
/// A channel-agnostic description of a finished batch, ready to be rendered into an
/// e-mail, a Slack/Teams message or a generic webhook payload.
/// </summary>
public sealed record BatchNotification(
    NotificationEvent Event,
    Guid JobId,
    string BranchKey,
    string InstanceKey,
    int Total,
    int Identical,
    int Differing,
    int Errors,
    int FilesWithContentErrors,
    bool Passed,
    IReadOnlyList<string> GateViolations,
    DateTimeOffset OccurredAt,
    string? Link = null) : INotification
{
    /// <summary>The whole batch notification is the structured webhook detail (includes <see cref="Link"/>).</summary>
    public object Detail => this;

    /// <summary>One-line headline suitable for an e-mail subject or chat title.</summary>
    public string Title => Event switch
    {
        NotificationEvent.Failed => $"diffpdf — batch FAILED: {BranchKey}/{InstanceKey}",
        NotificationEvent.GateViolated => $"diffpdf — gate VIOLATED: {BranchKey}/{InstanceKey}",
        NotificationEvent.CompletedWithErrors => $"diffpdf — batch completed WITH ERRORS: {BranchKey}/{InstanceKey}",
        _ => $"diffpdf — batch passed: {BranchKey}/{InstanceKey}",
    };

    /// <summary>Human-readable body with the headline counts, any gate violations and a deep link to the job.</summary>
    public string Summary
    {
        get
        {
            var lines = new List<string>
            {
                $"Job {JobId}",
                $"Files: {Total} total, {Identical} identical, {Differing} differing, {Errors} error(s), {FilesWithContentErrors} with content errors.",
            };
            if (GateViolations.Count > 0)
            {
                lines.Add("Gate violations:");
                lines.AddRange(GateViolations.Select(v => $"  - {v}"));
            }
            if (!string.IsNullOrWhiteSpace(Link))
                lines.Add($"Details: {Link}");
            lines.Add($"Finished at {OccurredAt:u}.");
            return string.Join('\n', lines);
        }
    }
}
