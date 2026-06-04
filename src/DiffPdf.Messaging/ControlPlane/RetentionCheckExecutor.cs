using DiffPdf.Core.Abstractions;
using DiffPdf.Core.Models;
using DiffPdf.Persistence;
using Microsoft.Extensions.Logging;

namespace DiffPdf.Messaging.ControlPlane;

/// <summary>
/// Prunes the on-disk artifacts (the <c>reports/{jobId}</c> folder) of finished jobs older than the
/// configured retention window. DB rows and run history are kept. Parameters: <c>retentionDays</c>
/// (default 30) and <c>maxPerTick</c> (default 100).
/// </summary>
public sealed class RetentionCheckExecutor(
    IJobStore jobs,
    IJobStoragePathProvider paths,
    ILogger<RetentionCheckExecutor> logger) : IControlCheckExecutor
{
    public CheckType Type => CheckType.Retention;

    public async Task<CheckResult> ExecuteAsync(ControlCheck check, CancellationToken ct)
    {
        int retentionDays = GetInt(check, "retentionDays", 30);
        int maxPerTick = GetInt(check, "maxPerTick", 100);
        if (retentionDays < 0)
            return CheckResult.Failed($"invalid retentionDays '{retentionDays}'.");

        var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromDays(retentionDays);
        var prunable = await jobs.ListPrunableArtifactsAsync(cutoff, maxPerTick, ct);
        if (prunable.Count == 0)
            return CheckResult.Ok($"nothing to prune (older than {retentionDays} d).");

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
            catch (Exception ex)
            {
                failed++;
                logger.LogWarning(ex, "Retention check: failed to prune artifacts for job {JobId}.", job.Id);
            }
        }

        return failed == 0
            ? CheckResult.Ok($"pruned artifacts of {pruned} job(s) completed before {cutoff:u}.")
            : CheckResult.Warning($"pruned {pruned} job(s); {failed} failed to prune.");
    }

    private static int GetInt(ControlCheck check, string key, int fallback) =>
        check.Parameters.TryGetValue(key, out var raw) && int.TryParse(raw, out var value) ? value : fallback;
}
