using DiffPdf.Core.Models;
using DiffPdf.Core.Storage;
using DiffPdf.Persistence;

namespace DiffPdf.Core.Tests;

public class InMemoryJobStoreTests
{
    private static ComparisonJob NewJob() => new()
    {
        Id = Guid.NewGuid(),
        BusinessInstanceId = Guid.NewGuid(),
        ProjectId = Guid.NewGuid(),
        Request = new BatchComparisonRequest
        {
            Scope = new JobScope("Alfa", "LamaEnergyAlfa"),
            OldFolder = "/old",
            NewFolder = "/new",
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

        var byInstance = await store.ListAsync(new JobListQuery { BusinessInstanceKey = "Alfa" });
        var other = await store.ListAsync(new JobListQuery { BusinessInstanceKey = "Nope" });

        Assert.Single(byInstance);
        Assert.Empty(other);
    }

    [Fact]
    public async Task UpdateRequest_OnlyWhenQueued()
    {
        var store = new InMemoryJobStore();
        var job = await store.CreateAsync(NewJob());

        var newRequest = job.Request with { OldFolder = "/changed" };
        var updated = await store.UpdateRequestAsync(job.Id, newRequest);
        Assert.NotNull(updated);
        Assert.Equal("/changed", updated!.Request.OldFolder);

        // Once running, update is rejected.
        await store.TryStartAsync(job.Id, "w", TimeSpan.FromMinutes(5));
        Assert.Null(await store.UpdateRequestAsync(job.Id, newRequest with { OldFolder = "/again" }));
    }

    [Fact]
    public async Task Pause_Resume_RoundTrip()
    {
        var store = new InMemoryJobStore();
        var job = await store.CreateAsync(NewJob());

        Assert.Null(await store.PauseAsync(job.Id)); // not running yet

        await store.TryStartAsync(job.Id, "w", TimeSpan.FromMinutes(5));
        var paused = await store.PauseAsync(job.Id);
        Assert.Equal(JobStatus.Paused, paused!.Status);

        Assert.Null(await store.ResumeAsync(Guid.NewGuid())); // missing
        var resumed = await store.ResumeAsync(job.Id);
        Assert.Equal(JobStatus.Running, resumed!.Status);
    }

    [Fact]
    public async Task Cancel_AllowedFromPaused()
    {
        var store = new InMemoryJobStore();
        var job = await store.CreateAsync(NewJob());
        await store.TryStartAsync(job.Id, "w", TimeSpan.FromMinutes(5));
        await store.PauseAsync(job.Id);

        var cancelled = await store.CancelAsync(job.Id);
        Assert.Equal(JobStatus.Cancelled, cancelled!.Status);
    }

    [Fact]
    public async Task Delete_OnlyWhenTerminal()
    {
        var store = new InMemoryJobStore();
        var job = await store.CreateAsync(NewJob());

        Assert.False(await store.DeleteAsync(job.Id)); // Queued = active

        var started = await store.TryStartAsync(job.Id, "w", TimeSpan.FromMinutes(5));
        await store.CompleteAsync(job.Id, Report(), started!.Version);

        Assert.True(await store.DeleteAsync(job.Id));
        Assert.Null(await store.GetAsync(job.Id));
    }
}
