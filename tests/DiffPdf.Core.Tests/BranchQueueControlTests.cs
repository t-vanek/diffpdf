using DiffPdf.Core.Abstractions;
using DiffPdf.Application.Configuration;
using DiffPdf.Application.Queue;
using DiffPdf.Core.Models;
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
        var control = new BranchQueueControl(branches, instances, jobs, new InMemoryFilePairTaskStore(), launcher, dispatcher, resolver, new FakeResume());

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
        var control = new BranchQueueControl(branches, instances, jobs, new InMemoryFilePairTaskStore(), new FakeBatchLauncher(jobs), dispatcher,
            new ScopeConfigurationResolver(new InMemoryScopeConfigurationStore()), new FakeResume());

        Assert.Null(await control.ActOnBranchAsync("Nope", QueueAction.Run));
        Assert.Null(await control.ActOnInstanceAsync("Nope", "Nada", QueueAction.Run));
    }

    [Fact]
    public async Task Stop_Instance_CancelsJob_AndSkipsItsQueuedPairs()
    {
        var jobs = new InMemoryJobStore();
        var tasks = new InMemoryFilePairTaskStore();
        var branches = new InMemoryBranchStore();
        var instances = new InMemoryInstanceStore();
        var dispatcher = new BranchQueueDispatcher(jobs, branches, new NullRunPublisher(), new NullBranchQueueStatePublisher(), new BranchDispatchLocks(), new DiffPdfMetrics(), NullLogger<BranchQueueDispatcher>.Instance);
        var control = new BranchQueueControl(branches, instances, jobs, tasks, new FakeBatchLauncher(jobs), dispatcher,
            new ScopeConfigurationResolver(new InMemoryScopeConfigurationStore()), new FakeResume());

        var b = await branches.CreateAsync("Alfa", "Alfa");
        var inst = await instances.CreateAsync(b.Id, "Lama", "Lama", "/base", null);

        // A running job for the instance with two un-started pairs + one already completed.
        var jobId = Guid.NewGuid();
        await jobs.CreateAsync(new ComparisonJob
        {
            Id = jobId, BranchId = b.Id, InstanceId = inst.Id, Status = JobStatus.Running,
            Request = new BatchComparisonRequest { Scope = new JobScope("Alfa", "Lama") },
        });
        await tasks.CreateManyAsync(
        [
            new FilePairTask { Id = Guid.NewGuid(), JobId = jobId, RelativePath = "a.pdf", Status = FilePairTaskStatus.Queued },
            new FilePairTask { Id = Guid.NewGuid(), JobId = jobId, RelativePath = "b.pdf", Status = FilePairTaskStatus.Queued },
            new FilePairTask { Id = Guid.NewGuid(), JobId = jobId, RelativePath = "c.pdf", Status = FilePairTaskStatus.Completed },
        ]);

        await control.ActOnInstanceAsync("Alfa", "Lama", QueueAction.Stop);

        Assert.Equal(JobStatus.Cancelled, (await jobs.GetAsync(jobId))!.Status);
        var after = await tasks.ListByJobAsync(jobId);
        Assert.Equal(2, after.Count(t => t.Status == FilePairTaskStatus.Skipped)); // the two Queued → Skipped
        Assert.Equal(1, after.Count(t => t.Status == FilePairTaskStatus.Completed)); // the done one untouched
        Assert.DoesNotContain(after, t => t.Status == FilePairTaskStatus.Queued);    // nothing left "pending"
    }
}
