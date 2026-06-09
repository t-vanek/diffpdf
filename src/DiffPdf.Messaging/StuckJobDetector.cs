using DiffPdf.Core.Models;

namespace DiffPdf.Messaging;

/// <summary>
/// Pure rule for the stuck-job watchdog: which Running jobs have stalled mid-comparison. A job is stalled when
/// it has finished indexing (<see cref="ComparisonJob.TotalCount"/> &gt; 0) but has not completed a pair within
/// the stall window — its <see cref="ComparisonJob.UpdatedAt"/> beacon (advanced on every pair completion) is
/// older than <paramref name="threshold"/>. Indexing-phase stalls (TotalCount == 0) are excluded: those are
/// recovered separately by the branch-queue dispatcher; done-but-finalizing jobs (Processed == Total) too.
/// </summary>
public static class StuckJobDetector
{
    public static IReadOnlyList<ComparisonJob> FindStalled(
        IReadOnlyList<ComparisonJob> runningJobs, DateTimeOffset now, TimeSpan threshold)
    {
        var cutoff = now - threshold;
        return runningJobs
            .Where(j => j.Status == JobStatus.Running
                && j.TotalCount > 0
                && j.ProcessedCount < j.TotalCount
                && LastProgress(j) < cutoff)
            .ToList();
    }

    /// <summary>The job's last liveness beacon: UpdatedAt, falling back to StartedAt then CreatedAt.</summary>
    public static DateTimeOffset LastProgress(ComparisonJob job) =>
        job.UpdatedAt ?? job.StartedAt ?? job.CreatedAt;
}
