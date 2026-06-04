using DiffPdf.Core.Abstractions;
using DiffPdf.Core.Models;
using DiffPdf.Messaging.Configuration;
using DiffPdf.Messaging.Observability;
using DiffPdf.Messaging.Scheduling;
using DiffPdf.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiffPdf.Core.Tests;

/// <summary>Queue control guards: an instance is never double-enqueued while it already has a pending/active job.</summary>
public class BranchQueueControlTests
{
    private sealed class FakeBatchLauncher(InMemoryJobStore jobs) : IBatchLauncher
    {
        public int Calls { get; private set; }
        public async Task<LaunchResult> LaunchAsync(string branchKey, string instanceKey, LaunchSpec spec, bool enqueueOnly = false, CancellationToken ct = default)
        {
            Calls++;
            var job = new ComparisonJob
            {
                Id = Guid.NewGuid(),
                BranchId = Guid.NewGuid(),
                InstanceId = Guid.NewGuid(),
                Status = enqueueOnly ? JobStatus.Draft : JobStatus.Queued,
                Priority = spec.Priority,
                Request = new BatchComparisonRequest { Scope = new JobScope(branchKey, instanceKey) },
            };
            await jobs.CreateAsync(job, ct);
            return new LaunchResult(LaunchOutcome.Launched, job.Id);
        }
    }

    private sealed class NullRunPublisher : IRunCommandPublisher
    {
        public Task PublishRunAsync(DiffPdf.Messaging.Messages.RunBatchComparison command, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeResume : IJobResumeService
    {
        public Task<(ComparisonJob? Job, int Redispatched)> ResumeAsync(Guid jobId, CancellationToken ct = default) =>
            Task.FromResult<(ComparisonJob?, int)>((null, 0));
    }

    [Fact]
    public async Task Enqueue_Twice_DoesNotDuplicate()
    {
        var jobs = new InMemoryJobStore();
        var branches = new InMemoryBranchStore();
        var instances = new InMemoryInstanceStore();
        var launcher = new FakeBatchLauncher(jobs);
        var dispatcher = new BranchQueueDispatcher(jobs, branches, new NullRunPublisher(), new NullBranchQueueStatePublisher(), new BranchDispatchLocks(), new DiffPdfMetrics(), NullLogger<BranchQueueDispatcher>.Instance);
        var resolver = new ScopeConfigurationResolver(new InMemoryScopeConfigurationStore());
        var control = new BranchQueueControl(branches, instances, jobs, launcher, dispatcher, resolver, new FakeResume());

        var b = await branches.CreateAsync("Alfa", "Alfa");
        await instances.CreateAsync(b.Id, "Lama", "Lama", "/base", null);

        var first = await control.ActOnInstanceAsync("Alfa", "Lama", QueueAction.Enqueue);
        var second = await control.ActOnInstanceAsync("Alfa", "Lama", QueueAction.Enqueue);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(1, launcher.Calls); // the second enqueue is skipped — the instance already has a pending job
        Assert.Contains("už", second!.Message ?? string.Empty);
    }

    [Fact]
    public async Task UnknownBranchOrInstance_ReturnsNull()
    {
        var jobs = new InMemoryJobStore();
        var branches = new InMemoryBranchStore();
        var instances = new InMemoryInstanceStore();
        var dispatcher = new BranchQueueDispatcher(jobs, branches, new NullRunPublisher(), new NullBranchQueueStatePublisher(), new BranchDispatchLocks(), new DiffPdfMetrics(), NullLogger<BranchQueueDispatcher>.Instance);
        var control = new BranchQueueControl(branches, instances, jobs, new FakeBatchLauncher(jobs), dispatcher,
            new ScopeConfigurationResolver(new InMemoryScopeConfigurationStore()), new FakeResume());

        Assert.Null(await control.ActOnBranchAsync("Nope", QueueAction.Run));
        Assert.Null(await control.ActOnInstanceAsync("Nope", "Nada", QueueAction.Run));
    }
}
