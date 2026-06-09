using DiffPdf.Core.Abstractions;
using DiffPdf.Core.Models;
using DiffPdf.Messaging;
using DiffPdf.Messaging.Messages;
using DiffPdf.Persistence;
using DiffPdf.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Wolverine;

namespace DiffPdf.Core.Tests;

/// <summary>
/// <see cref="WorkerLifecycleService"/> bookends a worker so an interrupted comparison recovers fast:
/// <c>StartAsync</c> reclaims orphaned Running pairs (republishes + flags their jobs "Obnoveno") when enabled;
/// <c>StopAsync</c> releases ONLY this process's in-flight pairs and — by design — publishes nothing (avoiding
/// the Wolverine shutdown teardown race; the requeue-to-Queued is what lets the redelivered envelope re-claim).
/// </summary>
public class WorkerLifecycleServiceTests
{
    private sealed class FixedWorkerId(string id) : IWorkerInstanceIdProvider
    {
        public string WorkerInstanceId { get; } = id;
    }

    private static FilePairTask Pair(Guid jobId, string name) => new()
    {
        Id = Guid.NewGuid(),
        JobId = jobId,
        RelativePath = name,
        OldFilePath = "/old/" + name,
        NewFilePath = "/new/" + name,
    };

    private static ComparisonJob RunningJob(Guid id) => new()
    {
        Id = id,
        BranchId = Guid.NewGuid(),
        InstanceId = Guid.NewGuid(),
        Status = JobStatus.Running,
        Request = new BatchComparisonRequest
        {
            Scope = new JobScope("Alfa", "Lama"),
            OldFolder = "/old",
            NewFolder = "/new",
            ReportsFolder = "/reports",
        },
    };

    private static WorkerLifecycleService Service(IFilePairTaskStore taskStore, IMessageBus bus, IJobStore jobStore, string workerId, bool reclaim)
    {
        var sc = new ServiceCollection();
        sc.AddSingleton(taskStore);
        sc.AddSingleton(bus);
        sc.AddSingleton(jobStore);
        var sp = sc.BuildServiceProvider();
        return new WorkerLifecycleService(
            sp.GetRequiredService<IServiceScopeFactory>(),
            new FixedWorkerId(workerId),
            Options.Create(new WorkerOptions { ReclaimOrphansOnStartup = reclaim }),
            NullLogger<WorkerLifecycleService>.Instance);
    }

    [Fact]
    public async Task StartAsync_reclaims_orphans_republishes_and_marks_recovered_when_enabled()
    {
        var taskStore = new InMemoryFilePairTaskStore();
        var jobStore = new InMemoryJobStore();
        var bus = new RecordingMessageBus();
        var job = await jobStore.CreateAsync(RunningJob(Guid.NewGuid()));
        var task = Pair(job.Id, "a.pdf");
        await taskStore.CreateManyAsync([task]);
        await taskStore.TryClaimAsync(task.Id, "dead-worker", TimeSpan.FromMinutes(5)); // orphan: Running

        await Service(taskStore, bus, jobStore, "new-worker", reclaim: true).StartAsync(CancellationToken.None);

        var dispatched = Assert.Single(bus.Published.OfType<CompareFilePair>());
        Assert.Equal(task.Id, dispatched.TaskId);
        Assert.NotNull(await taskStore.TryClaimAsync(task.Id, "w", TimeSpan.FromMinutes(5))); // was returned to Queued
        Assert.NotNull((await jobStore.GetAsync(job.Id))!.RecoveredAt);                       // job flagged → "Obnoveno" chip
    }

    [Fact]
    public async Task StartAsync_does_nothing_when_disabled()
    {
        var taskStore = new InMemoryFilePairTaskStore();
        var jobStore = new InMemoryJobStore();
        var bus = new RecordingMessageBus();
        var job = await jobStore.CreateAsync(RunningJob(Guid.NewGuid()));
        var task = Pair(job.Id, "a.pdf");
        await taskStore.CreateManyAsync([task]);
        await taskStore.TryClaimAsync(task.Id, "dead-worker", TimeSpan.FromMinutes(5));

        await Service(taskStore, bus, jobStore, "new-worker", reclaim: false).StartAsync(CancellationToken.None);

        Assert.Empty(bus.Published);
        Assert.Null(await taskStore.TryClaimAsync(task.Id, "w", TimeSpan.FromMinutes(5))); // still Running (untouched)
        Assert.Null((await jobStore.GetAsync(job.Id))!.RecoveredAt);                        // not flagged
    }

    [Fact]
    public async Task StopAsync_releases_only_this_workers_pairs_and_publishes_nothing()
    {
        var taskStore = new InMemoryFilePairTaskStore();
        var jobStore = new InMemoryJobStore();
        var bus = new RecordingMessageBus();
        var jobId = Guid.NewGuid();
        var mine = Pair(jobId, "mine.pdf");
        var foreign = Pair(jobId, "foreign.pdf");
        await taskStore.CreateManyAsync([mine, foreign]);
        await taskStore.TryClaimAsync(mine.Id, "me", TimeSpan.FromMinutes(5));
        await taskStore.TryClaimAsync(foreign.Id, "other", TimeSpan.FromMinutes(5));

        await Service(taskStore, bus, jobStore, "me", reclaim: true).StopAsync(CancellationToken.None);

        Assert.Empty(bus.Published); // NO publish during shutdown (teardown-race guard)
        Assert.NotNull(await taskStore.TryClaimAsync(mine.Id, "w", TimeSpan.FromMinutes(5)));  // released → Queued
        Assert.Null(await taskStore.TryClaimAsync(foreign.Id, "w", TimeSpan.FromMinutes(5)));  // foreign untouched → Running
    }
}
