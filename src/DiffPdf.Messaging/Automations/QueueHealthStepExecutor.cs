using DiffPdf.Core.Models;
using DiffPdf.Persistence;

namespace DiffPdf.Messaging.Automations;

/// <summary>
/// Monitors the comparison queue and notifies on backlog or stalls — read-only, it never changes a job.
/// Two signals: the <b>backlog depth</b> (jobs in <see cref="JobStatus.Queued"/>) against
/// <c>maxQueuedWarn</c>/<c>maxQueuedFail</c>, and <b>stalled jobs</b> — anything in
/// <see cref="JobStatus.Running"/> whose <c>StartedAt</c> is older than <c>stalledMinutes</c>. A stalled job
/// is a Failure; a deep backlog is a Warning (or Failure past the fail threshold).
/// Parameters: <c>maxQueuedWarn</c> (50), <c>maxQueuedFail</c> (200), <c>stalledMinutes</c> (30).
/// </summary>
public sealed class QueueHealthStepExecutor(IJobStore jobs) : IAutomationStepExecutor
{
    public AutomationStepType Type => AutomationStepType.QueueHealth;

    public async Task<StepResult> ExecuteAsync(Automation automation, AutomationStep step, CancellationToken ct)
    {
        int maxQueuedWarn = StepParameters.GetInt(step, "maxQueuedWarn", 50);
        int maxQueuedFail = StepParameters.GetInt(step, "maxQueuedFail", 200);
        int stalledMinutes = StepParameters.GetInt(step, "stalledMinutes", 30);

        var counts = await jobs.CountByStatusAsync(ct);
        int queued = counts.TryGetValue(JobStatus.Queued, out var q) ? q : 0;
        int running = counts.TryGetValue(JobStatus.Running, out var r) ? r : 0;

        var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(stalledMinutes);
        var active = await jobs.ListAllActiveAndDraftAsync(ct);
        var stalled = active
            .Where(j => j.Status == JobStatus.Running && j.StartedAt is { } s && s < cutoff)
            .ToList();

        string summary = $"queued {queued}, running {running}";

        if (stalled.Count > 0)
        {
            var ids = string.Join(", ", stalled.Take(5).Select(j => $"{j.InstanceKey}:{j.Id.ToString()[..8]}"));
            return StepResult.Failed(
                $"{stalled.Count} job(s) running longer than {stalledMinutes} min ({ids}); {summary}.");
        }

        if (queued >= maxQueuedFail)
            return StepResult.Failed($"backlog too deep — {summary}; at/over {maxQueuedFail}.");
        if (queued >= maxQueuedWarn)
            return StepResult.Warning($"backlog building up — {summary}; at/over {maxQueuedWarn}.");

        return StepResult.Ok($"queue healthy — {summary}.");
    }
}
