using DiffPdf.Core.Abstractions;
using DiffPdf.Core.Models;
using DiffPdf.Messaging.Messages;
using DiffPdf.Messaging.Scheduling;
using DiffPdf.Persistence;

namespace DiffPdf.Core.Tests;

/// <summary>
/// Resume durability: a resumed job re-dispatches its still-queued pairs via a SINGLE durable, idempotent
/// <see cref="IndexBatch"/> (IndexBatchHandler's already-indexed path) rather than N un-tracked
/// <see cref="CompareFilePair"/> publishes — shrinking the mid-resume crash window from N messages to 1.
/// A non-paused job is a no-op.
/// </summary>
public class JobResumeServiceTests
{
    private static ComparisonJob RunningJob() => new()
    {
        Id = Guid.NewGuid(),
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

    private static FilePairTask Pair(Guid jobId) => new()
    {
        Id = Guid.NewGuid(),
        JobId = jobId,
        RelativePath = "a.pdf",
        OldFilePath = "/old/a.pdf",
        NewFilePath = "/new/a.pdf",
    };

    [Fact]
    public async Task Resume_publishes_one_IndexBatch_and_no_per_pair_messages()
    {
        var jobStore = new InMemoryJobStore();
        var taskStore = new InMemoryFilePairTaskStore();
        var bus = new RecordingMessageBus();
        var svc = new JobResumeService(jobStore, taskStore, new NullJobProgressPublisher(), bus);

        var job = await jobStore.CreateAsync(RunningJob());
        var p1 = Pair(job.Id);
        var p2 = Pair(job.Id);
        var p3 = Pair(job.Id);
        await taskStore.CreateManyAsync([p1, p2, p3]);
        // p1 finished while paused; p2 + p3 are still Queued → 2 pending.
        await taskStore.TryClaimAsync(p1.Id, "w", TimeSpan.FromMinutes(5));
        await taskStore.CompleteAsync(p1.Id, new FilePairResult { RelativePath = p1.RelativePath }, FilePairTaskStatus.Completed);
        await jobStore.PauseAsync(job.Id);

        var (resumed, redispatched) = await svc.ResumeAsync(job.Id);

        Assert.NotNull(resumed);
        Assert.Equal(JobStatus.Running, resumed!.Status);
        Assert.Equal(2, redispatched);                                  // the 2 Queued pairs
        Assert.Single(bus.Published.OfType<IndexBatch>());              // exactly one durable message
        Assert.DoesNotContain(bus.Published, m => m is CompareFilePair); // none of the old N publishes
        Assert.DoesNotContain(bus.Published, m => m is FinalizeBatch);
    }

    [Fact]
    public async Task Resume_of_non_paused_job_is_a_noop()
    {
        var jobStore = new InMemoryJobStore();
        var taskStore = new InMemoryFilePairTaskStore();
        var bus = new RecordingMessageBus();
        var svc = new JobResumeService(jobStore, taskStore, new NullJobProgressPublisher(), bus);

        var job = await jobStore.CreateAsync(RunningJob()); // Running, never paused

        var (resumed, redispatched) = await svc.ResumeAsync(job.Id);

        Assert.Null(resumed);
        Assert.Equal(0, redispatched);
        Assert.Empty(bus.Published);
    }
}
