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

    [Fact]
    public async Task Pause_Resume_RoundTrip_IsOneShotEachWay()
    {
        var store = new InMemoryJobStore();
        var job = await store.CreateAsync(NewJob());
        await store.TryStartAsync(job.Id, "w", TimeSpan.FromMinutes(5)); // Queued -> Running

        var paused = await store.PauseAsync(job.Id);
        Assert.Equal(JobStatus.Paused, paused!.Status);
        Assert.Null(await store.PauseAsync(job.Id)); // already paused

        var resumed = await store.ResumeAsync(job.Id);
        Assert.Equal(JobStatus.Running, resumed!.Status);
        Assert.Null(await store.ResumeAsync(job.Id)); // already running
    }

    [Fact]
    public async Task Pause_RequiresRunning()
    {
        var store = new InMemoryJobStore();
        var job = await store.CreateAsync(NewJob()); // Queued, not Running
        Assert.Null(await store.PauseAsync(job.Id));
    }

    [Fact]
    public async Task Cancel_IsAllowedFromPaused()
    {
        var store = new InMemoryJobStore();
        var job = await store.CreateAsync(NewJob());
        await store.TryStartAsync(job.Id, "w", TimeSpan.FromMinutes(5));
        await store.PauseAsync(job.Id);

        var cancelled = await store.CancelAsync(job.Id);
        Assert.Equal(JobStatus.Cancelled, cancelled!.Status);
    }

    [Fact]
    public async Task CountByStatus_GroupsJobs()
    {
        var store = new InMemoryJobStore();
        await store.CreateAsync(NewJob());                                   // Queued
        await store.CreateAsync(NewJob());                                   // Queued
        var running = await store.CreateAsync(NewJob());
        await store.TryStartAsync(running.Id, "w", TimeSpan.FromMinutes(5)); // -> Running

        var counts = await store.CountByStatusAsync();

        Assert.Equal(2, counts[JobStatus.Queued]);
        Assert.Equal(1, counts[JobStatus.Running]);
        Assert.False(counts.ContainsKey(JobStatus.Failed));
    }
}
