using DiffPdf.Core.Abstractions;
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
    public async Task ListStaleUnindexedRunning_FindsRunningNeverIndexedWithExpiredLease()
    {
        var store = new InMemoryJobStore();
        var job = await store.CreateAsync(NewJob());
        await store.TryStartAsync(job.Id, "w", TimeSpan.FromMinutes(5)); // Running, TotalCount 0, lease ~now+5min

        Assert.Empty(await store.ListStaleUnindexedRunningAsync(DateTimeOffset.UtcNow, 10)); // lease still in the future
        var stale = await store.ListStaleUnindexedRunningAsync(DateTimeOffset.UtcNow.AddMinutes(10), 10);
        Assert.Single(stale);
        Assert.Equal(job.Id, stale[0].Id);
    }

    [Fact]
    public async Task ListStaleUnindexedRunning_IgnoresIndexedJobs()
    {
        var store = new InMemoryJobStore();
        var job = await store.CreateAsync(NewJob());
        await store.TryStartAsync(job.Id, "w", TimeSpan.FromMinutes(5));
        await store.SetTotalAsync(job.Id, 3); // indexing produced tasks → recoverable by task recovery, not "stuck"

        Assert.Empty(await store.ListStaleUnindexedRunningAsync(DateTimeOffset.UtcNow.AddMinutes(10), 10));
    }

    [Fact]
    public async Task ListRunningFullyProcessed_FindsRunningJobWhereAllPairsDone()
    {
        var store = new InMemoryJobStore();
        var job = await store.CreateAsync(NewJob());
        await store.TryStartAsync(job.Id, "w", TimeSpan.FromMinutes(5));
        await store.SetTotalAsync(job.Id, 2);
        await store.IncrementProcessedAsync(job.Id);
        await store.IncrementProcessedAsync(job.Id); // processed == total == 2, but still Running (finalize lost)

        Assert.Empty(await store.ListRunningFullyProcessedAsync(DateTimeOffset.UtcNow.AddMinutes(-10), 10)); // just updated → not idle yet
        var pending = await store.ListRunningFullyProcessedAsync(DateTimeOffset.UtcNow.AddMinutes(10), 10);   // force-stale cutoff
        Assert.Single(pending);
        Assert.Equal(job.Id, pending[0].Id);
    }

    [Fact]
    public async Task ListRunningFullyProcessed_IgnoresStillProcessingJobs()
    {
        var store = new InMemoryJobStore();
        var job = await store.CreateAsync(NewJob());
        await store.TryStartAsync(job.Id, "w", TimeSpan.FromMinutes(5));
        await store.SetTotalAsync(job.Id, 2);
        await store.IncrementProcessedAsync(job.Id); // 1 of 2 — not done

        Assert.Empty(await store.ListRunningFullyProcessedAsync(DateTimeOffset.UtcNow.AddMinutes(10), 10));
    }

    [Fact]
    public async Task ListRunningFullyProcessed_IgnoresUnindexedJobs()
    {
        var store = new InMemoryJobStore();
        var job = await store.CreateAsync(NewJob());
        await store.TryStartAsync(job.Id, "w", TimeSpan.FromMinutes(5)); // TotalCount 0; 0 >= 0 must NOT match (excluded by TotalCount > 0)

        Assert.Empty(await store.ListRunningFullyProcessedAsync(DateTimeOffset.UtcNow.AddMinutes(10), 10));
    }

    [Fact]
    public async Task ListRunningFullyProcessed_IgnoresTerminalJobs()
    {
        var store = new InMemoryJobStore();
        var job = await store.CreateAsync(NewJob());
        await store.TryStartAsync(job.Id, "w", TimeSpan.FromMinutes(5));
        await store.SetTotalAsync(job.Id, 1);
        await store.IncrementProcessedAsync(job.Id);
        await store.CompleteAsync(job.Id, Report(), (await store.GetAsync(job.Id))!.Version); // Completed, not Running

        Assert.Empty(await store.ListRunningFullyProcessedAsync(DateTimeOffset.UtcNow.AddMinutes(10), 10));
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

    [Fact]
    public async Task MarkRecovered_StampsRecoveredAt_WriteOnce_AndSurfacesInSummary()
    {
        var store = new InMemoryJobStore();
        var job = await store.CreateAsync(NewJob());
        Assert.Null((await store.GetAsync(job.Id))!.RecoveredAt); // not recovered initially

        await store.MarkRecoveredAsync([job.Id]);
        var firstStamp = (await store.GetAsync(job.Id))!.RecoveredAt;
        Assert.NotNull(firstStamp);

        // The list projection carries it → drives the client's "Obnoveno" chip.
        var summary = Assert.Single(await store.ListSummariesAsync(new JobListQuery { BranchKey = "Alfa" }));
        Assert.Equal(firstStamp, summary.RecoveredAt);

        // Write-once: a later recovery leaves the first stamp.
        await store.MarkRecoveredAsync([job.Id]);
        Assert.Equal(firstStamp, (await store.GetAsync(job.Id))!.RecoveredAt);
    }

    [Fact]
    public async Task MarkRecovered_IgnoresUnknownIds_AndEmpty()
    {
        var store = new InMemoryJobStore();
        var job = await store.CreateAsync(NewJob());

        await store.MarkRecoveredAsync([]);                 // no-op
        await store.MarkRecoveredAsync([Guid.NewGuid()]);   // unknown id → no throw, no effect
        Assert.Null((await store.GetAsync(job.Id))!.RecoveredAt);
    }

    [Fact]
    public void JobProgressChanged_From_CarriesRecoveredAt()
    {
        var when = DateTimeOffset.UtcNow;
        var job = NewJob() with { Status = JobStatus.Running, RecoveredAt = when };

        var progress = JobProgressChanged.From(job);

        Assert.Equal(when, progress.RecoveredAt); // flag rides the realtime push → chip + toast
    }
}
