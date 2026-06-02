using DiffPdf.Core.Models;
using DiffPdf.Core.Storage;
using DiffPdf.Persistence;

namespace DiffPdf.Core.Tests;

public class InMemoryJobStoreTests
{
    private static ComparisonJob NewJob() => new()
    {
        Id = Guid.NewGuid(),
        BranchId = Guid.NewGuid(),
        InstanceId = Guid.NewGuid(),
        Request = new BatchComparisonRequest
        {
            Scope = new JobScope("Alfa", "LamaEnergy"),
            OldFolder = "/old",
            NewFolder = "/new",
            ReportsFolder = "/reports",
        },
    };

    private static BatchComparisonReport Report() => new() { OldFolder = "/old", NewFolder = "/new" };

    [Fact]
    public async Task TryStart_OnlyOneWorkerWins()
    {
        var store = new InMemoryJobStore();
        var job = await store.CreateAsync(NewJob());

        var first = await store.TryStartAsync(job.Id, "worker-1", TimeSpan.FromMinutes(5));
        var second = await store.TryStartAsync(job.Id, "worker-2", TimeSpan.FromMinutes(5));

        Assert.NotNull(first);
        Assert.Null(second); // already Running
        Assert.Equal(JobStatus.Running, first!.Status);
    }

    [Fact]
    public async Task Complete_RequiresMatchingVersion()
    {
        var store = new InMemoryJobStore();
        var job = await store.CreateAsync(NewJob());
        var started = await store.TryStartAsync(job.Id, "w", TimeSpan.FromMinutes(5));

        await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => store.CompleteAsync(job.Id, Report(), started!.Version + 99));

        var completed = await store.CompleteAsync(job.Id, Report(), started!.Version);
        Assert.Equal(JobStatus.Completed, completed.Status);
    }

    [Fact]
    public async Task Progress_DoesNotOverwriteCompleted()
    {
        var store = new InMemoryJobStore();
        var job = await store.CreateAsync(NewJob());
        var started = await store.TryStartAsync(job.Id, "w", TimeSpan.FromMinutes(5));
        var completed = await store.CompleteAsync(job.Id, Report(), started!.Version);

        await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => store.UpdateProgressAsync(job.Id, 1, 10, completed.Version));
    }

    [Fact]
    public async Task List_FiltersByScope()
    {
        var store = new InMemoryJobStore();
        await store.CreateAsync(NewJob());

        var byInstance = await store.ListAsync(new JobListQuery { BranchKey = "Alfa" });
        var other = await store.ListAsync(new JobListQuery { BranchKey = "Nope" });

        Assert.Single(byInstance);
        Assert.Empty(other);
    }

    [Fact]
    public async Task Enqueue_MovesDraftToQueued_AndIsOneShot()
    {
        var store = new InMemoryJobStore();
        var job = await store.CreateAsync(NewJob() with { Status = JobStatus.Draft });

        var queued = await store.EnqueueAsync(job.Id);
        Assert.NotNull(queued);
        Assert.Equal(JobStatus.Queued, queued!.Status);

        Assert.Null(await store.EnqueueAsync(job.Id)); // no longer Draft
    }

    [Fact]
    public async Task Cancel_IsAllowedFromDraft()
    {
        var store = new InMemoryJobStore();
        var job = await store.CreateAsync(NewJob() with { Status = JobStatus.Draft });

        var cancelled = await store.CancelAsync(job.Id);
        Assert.NotNull(cancelled);
        Assert.Equal(JobStatus.Cancelled, cancelled!.Status);
    }
}
