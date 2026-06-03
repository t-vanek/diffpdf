using DiffPdf.Core.Models;
using DiffPdf.Persistence;

namespace DiffPdf.Core.Tests;

public class JobArtifactRetentionTests
{
    private static ComparisonJob Job(JobStatus status, DateTimeOffset? completedAt) => new()
    {
        Id = Guid.NewGuid(),
        Request = new BatchComparisonRequest { Scope = new JobScope("Alfa", "Lama") },
        BranchId = Guid.NewGuid(),
        InstanceId = Guid.NewGuid(),
        Status = status,
        CompletedAt = completedAt,
    };

    [Fact]
    public async Task ListPrunable_OnlyFinished_BeforeCutoff_NotYetPruned()
    {
        var store = new InMemoryJobStore();
        var cutoff = DateTimeOffset.UtcNow.AddDays(-30);

        var old = await store.CreateAsync(Job(JobStatus.Completed, DateTimeOffset.UtcNow.AddDays(-40))); // prunable
        await store.CreateAsync(Job(JobStatus.Completed, DateTimeOffset.UtcNow.AddDays(-1)));             // too recent
        await store.CreateAsync(Job(JobStatus.Running, null));                                            // not finished

        var prunable = await store.ListPrunableArtifactsAsync(cutoff, 100);

        Assert.Single(prunable);
        Assert.Equal(old.Id, prunable[0].Id);
    }

    [Fact]
    public async Task MarkArtifactsPruned_ExcludesFromNextList()
    {
        var store = new InMemoryJobStore();
        var cutoff = DateTimeOffset.UtcNow.AddDays(-30);
        var job = await store.CreateAsync(Job(JobStatus.Failed, DateTimeOffset.UtcNow.AddDays(-40)));

        Assert.Single(await store.ListPrunableArtifactsAsync(cutoff, 100));

        await store.MarkArtifactsPrunedAsync(job.Id, DateTimeOffset.UtcNow);

        Assert.Empty(await store.ListPrunableArtifactsAsync(cutoff, 100));
    }
}
