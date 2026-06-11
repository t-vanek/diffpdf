using DiffPdf.Core.Abstractions;
using DiffPdf.Core.Models;
using DiffPdf.Persistence;
using Microsoft.Extensions.Logging;

namespace DiffPdf.Messaging.Automations;

/// <summary>
/// Prunes the on-disk artifacts (the <c>reports/{jobId}</c> folder) of finished jobs older than the
/// configured retention window. DB rows and run history are kept. Parameters: <c>retentionDays</c>
/// (default 30) and <c>maxPerTick</c> (default 100).
/// </summary>
public sealed class RetentionStepExecutor(
    IJobStore jobs,
    IJobStoragePathProvider paths,
    ILogger<RetentionStepExecutor> logger) : IAutomationStepExecutor
{
    public AutomationStepType Type => AutomationStepType.Retention;

    public async Task<StepResult> ExecuteAsync(Automation automation, AutomationStep step, CancellationToken ct)
    {
        int retentionDays = StepParameters.GetInt(step, "retentionDays", 30);
        int maxPerTick = StepParameters.GetInt(step, "maxPerTick", 100);
        if (retentionDays < 0)
            return StepResult.Failed($"invalid retentionDays '{retentionDays}'.");

        var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromDays(retentionDays);
        var prunable = await jobs.ListPrunableArtifactsAsync(cutoff, maxPerTick, ct);
        if (prunable.Count == 0)
            return StepResult.Ok($"nothing to prune (older than {retentionDays} d).");

        int pruned = 0, failed = 0;
        foreach (var job in prunable)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                string root = paths.GetJobRoot(job);
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
                await jobs.MarkArtifactsPrunedAsync(job.Id, DateTimeOffset.UtcNow, ct);
                pruned++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failed++;
                logger.LogWarning(ex, "Retention step: failed to prune artifacts for job {JobId}.", job.Id);
            }
        }

        return failed == 0
            ? StepResult.Ok($"pruned artifacts of {pruned} job(s) completed before {cutoff:u}.")
            : StepResult.Warning($"pruned {pruned} job(s); {failed} failed to prune.");
    }
}

/// <summary>Shared parameter parsing for step executors.</summary>
internal static class StepParameters
{
    public static int GetInt(AutomationStep step, string key, int fallback) =>
        step.Parameters.TryGetValue(key, out var raw) && int.TryParse(raw, out var value) ? value : fallback;

    public static string GetString(AutomationStep step, string key, string fallback) =>
        step.Parameters.TryGetValue(key, out var raw) && !string.IsNullOrWhiteSpace(raw) ? raw.Trim() : fallback;
}
