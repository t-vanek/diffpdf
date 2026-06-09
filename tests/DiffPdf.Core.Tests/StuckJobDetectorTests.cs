using DiffPdf.Core.Models;
using DiffPdf.Messaging;

namespace DiffPdf.Core.Tests;

public class StuckJobDetectorTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 9, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Threshold = TimeSpan.FromMinutes(30);

    private static ComparisonJob Job(
        JobStatus status = JobStatus.Running, int total = 10, int processed = 3,
        DateTimeOffset? updatedAt = null, DateTimeOffset? startedAt = null) => new()
    {
        Id = Guid.NewGuid(),
        BranchId = Guid.NewGuid(),
        InstanceId = Guid.NewGuid(),
        Request = new BatchComparisonRequest { Scope = new JobScope("b", "i") },
        Status = status,
        TotalCount = total,
        ProcessedCount = processed,
        UpdatedAt = updatedAt,
        StartedAt = startedAt,
        CreatedAt = Now.AddHours(-2),
    };

    private static IReadOnlyList<ComparisonJob> Find(params ComparisonJob[] jobs) =>
        StuckJobDetector.FindStalled(jobs, Now, Threshold);

    [Fact]
    public void Stalled_when_running_indexed_and_no_progress_past_threshold()
    {
        var job = Job(updatedAt: Now.AddMinutes(-40));
        Assert.Same(job, Assert.Single(Find(job)));
    }

    [Fact]
    public void Not_stalled_when_progress_is_recent() =>
        Assert.Empty(Find(Job(updatedAt: Now.AddMinutes(-5))));

    [Fact]
    public void Not_stalled_during_indexing_phase() => // TotalCount == 0: handled by the branch-queue dispatcher
        Assert.Empty(Find(Job(total: 0, processed: 0, updatedAt: Now.AddHours(-1))));

    [Fact]
    public void Not_stalled_when_all_pairs_done() => // Processed == Total: finished, awaiting finalize
        Assert.Empty(Find(Job(total: 10, processed: 10, updatedAt: Now.AddHours(-1))));

    [Fact]
    public void Not_stalled_when_not_running() => // Paused/terminal are excluded
        Assert.Empty(Find(Job(status: JobStatus.Paused, updatedAt: Now.AddHours(-1))));

    [Fact]
    public void Falls_back_to_StartedAt_when_UpdatedAt_is_null()
    {
        var job = Job(updatedAt: null, startedAt: Now.AddMinutes(-45));
        Assert.Same(job, Assert.Single(Find(job)));
    }

    [Fact]
    public void Exactly_at_threshold_is_not_stalled() => // strict "<" cutoff
        Assert.Empty(Find(Job(updatedAt: Now - Threshold)));
}
