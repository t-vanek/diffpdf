using DiffPdf.Core.Models;
using DiffPdf.Persistence;

namespace DiffPdf.Core.Tests;

public class FilePairTaskStoreTests
{
    private static FilePairTask Task(Guid jobId) => new()
    {
        Id = Guid.NewGuid(),
        JobId = jobId,
        RelativePath = "a/b.pdf",
        OldFilePath = "/old/a/b.pdf",
        NewFilePath = "/new/a/b.pdf",
    };

    [Fact]
    public async Task Claim_OnlyOnce()
    {
        var store = new InMemoryFilePairTaskStore();
        var jobId = Guid.NewGuid();
        var task = Task(jobId);
        await store.CreateManyAsync([task]);

        var first = await store.TryClaimAsync(task.Id, "w1", TimeSpan.FromMinutes(5));
        var second = await store.TryClaimAsync(task.Id, "w2", TimeSpan.FromMinutes(5));

        Assert.NotNull(first);
        Assert.Null(second);
        Assert.Equal(FilePairTaskStatus.Running, first!.Status);
    }

    [Fact]
    public async Task Requeue_AllowsReclaim()
    {
        var store = new InMemoryFilePairTaskStore();
        var task = Task(Guid.NewGuid());
        await store.CreateManyAsync([task]);
        await store.TryClaimAsync(task.Id, "w1", TimeSpan.FromMinutes(5));

        await store.RequeueAsync(task.Id);
        var reclaimed = await store.TryClaimAsync(task.Id, "w2", TimeSpan.FromMinutes(5));

        Assert.NotNull(reclaimed);
    }

    [Fact]
    public async Task RequeueStale_RecoversExpiredLeaseTasks()
    {
        var store = new InMemoryFilePairTaskStore();
        var jobId = Guid.NewGuid();
        var task = Task(jobId);
        await store.CreateManyAsync([task]);
        await store.TryClaimAsync(task.Id, "dead-worker", TimeSpan.FromSeconds(-1)); // already expired

        var recovered = await store.RequeueStaleAsync();

        var one = Assert.Single(recovered);
        Assert.Equal((jobId, task.Id), one);
        Assert.NotNull(await store.TryClaimAsync(task.Id, "w2", TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public async Task Complete_StoresResult()
    {
        var store = new InMemoryFilePairTaskStore();
        var jobId = Guid.NewGuid();
        var task = Task(jobId);
        await store.CreateManyAsync([task]);
        await store.TryClaimAsync(task.Id, "w", TimeSpan.FromMinutes(5));

        var result = new FilePairResult { RelativePath = task.RelativePath, Status = FilePairStatus.Differs, DifferingPages = 2 };
        await store.CompleteAsync(task.Id, result, FilePairTaskStatus.Completed);

        var listed = await store.ListByJobAsync(jobId);
        var one = Assert.Single(listed);
        Assert.Equal(FilePairTaskStatus.Completed, one.Status);
        Assert.Equal(2, one.Result!.DifferingPages);
    }

    [Fact]
    public async Task Complete_OnlyTransitionsRunning_DuplicateIsNoOp()
    {
        var store = new InMemoryFilePairTaskStore();
        var task = Task(Guid.NewGuid());
        await store.CreateManyAsync([task]);
        await store.TryClaimAsync(task.Id, "w", TimeSpan.FromMinutes(5));

        var first = new FilePairResult { RelativePath = task.RelativePath, Status = FilePairStatus.Differs, DifferingPages = 2 };
        Assert.True(await store.CompleteAsync(task.Id, first, FilePairTaskStatus.Completed)); // won the Running→Completed transition

        // A duplicate/late completion finds the task already terminal — returns false and must not overwrite the result.
        var second = new FilePairResult { RelativePath = task.RelativePath, Status = FilePairStatus.Identical, DifferingPages = 99 };
        Assert.False(await store.CompleteAsync(task.Id, second, FilePairTaskStatus.Completed));

        var stored = Assert.Single(await store.ListByJobAsync(task.JobId));
        Assert.Equal(FilePairStatus.Differs, stored.Result!.Status);
        Assert.Equal(2, stored.Result!.DifferingPages);
    }

    [Fact]
    public async Task RequeueRunning_Null_RequeuesAllRunning_AndAllowsReclaim()
    {
        var store = new InMemoryFilePairTaskStore();
        var jobId = Guid.NewGuid();
        var t1 = Task(jobId);
        var t2 = Task(jobId);
        await store.CreateManyAsync([t1, t2]);
        await store.TryClaimAsync(t1.Id, "w1", TimeSpan.FromMinutes(5));
        await store.TryClaimAsync(t2.Id, "w2", TimeSpan.FromMinutes(5));

        var requeued = await store.RequeueRunningTasksAsync(lockedBy: null);

        Assert.Equal(2, requeued.Count);
        Assert.Contains((jobId, t1.Id), requeued);
        Assert.Contains((jobId, t2.Id), requeued);
        Assert.NotNull(await store.TryClaimAsync(t1.Id, "w3", TimeSpan.FromMinutes(5))); // returned to Queued → re-claimable
        Assert.NotNull(await store.TryClaimAsync(t2.Id, "w3", TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public async Task RequeueRunning_FiltersByWorker_LeavesForeignRunning()
    {
        var store = new InMemoryFilePairTaskStore();
        var jobId = Guid.NewGuid();
        var mine = Task(jobId);
        var foreign = Task(jobId);
        await store.CreateManyAsync([mine, foreign]);
        await store.TryClaimAsync(mine.Id, "me", TimeSpan.FromMinutes(5));
        await store.TryClaimAsync(foreign.Id, "other", TimeSpan.FromMinutes(5));

        var requeued = await store.RequeueRunningTasksAsync(lockedBy: "me");

        Assert.Equal((jobId, mine.Id), Assert.Single(requeued));
        Assert.NotNull(await store.TryClaimAsync(mine.Id, "w", TimeSpan.FromMinutes(5)));  // released → Queued
        Assert.Null(await store.TryClaimAsync(foreign.Id, "w", TimeSpan.FromMinutes(5)));  // untouched → still Running
    }

    [Fact]
    public async Task RequeueRunning_IgnoresQueuedAndTerminal()
    {
        var store = new InMemoryFilePairTaskStore();
        var jobId = Guid.NewGuid();
        var queued = Task(jobId);
        var running = Task(jobId);
        var done = Task(jobId);
        await store.CreateManyAsync([queued, running, done]);
        await store.TryClaimAsync(running.Id, "w", TimeSpan.FromMinutes(5));
        await store.TryClaimAsync(done.Id, "w", TimeSpan.FromMinutes(5));
        await store.CompleteAsync(done.Id, new FilePairResult { RelativePath = done.RelativePath }, FilePairTaskStatus.Completed);

        var requeued = await store.RequeueRunningTasksAsync(lockedBy: null);

        Assert.Equal((jobId, running.Id), Assert.Single(requeued)); // only the Running one
    }

    [Fact]
    public async Task RequeueRunning_Empty_WhenNoneRunning()
    {
        var store = new InMemoryFilePairTaskStore();
        await store.CreateManyAsync([Task(Guid.NewGuid())]); // Queued only
        Assert.Empty(await store.RequeueRunningTasksAsync(lockedBy: null));
    }

    [Fact]
    public async Task ListByJobPaged_PagesAndReportsTotal()
    {
        var store = new InMemoryFilePairTaskStore();
        var jobId = Guid.NewGuid();
        await store.CreateManyAsync(Enumerable.Range(0, 5).Select(i => new FilePairTask
        {
            Id = Guid.NewGuid(), JobId = jobId, RelativePath = $"f{i}.pdf", OldFilePath = "/o", NewFilePath = "/n",
        }).ToList());

        var (page1, total) = await store.ListByJobPagedAsync(jobId, limit: 2, offset: 0, search: null, onlyDiffering: false);
        Assert.Equal(5, total);
        Assert.Equal(["f0.pdf", "f1.pdf"], page1.Select(t => t.RelativePath)); // ordered by name, page window

        var (page3, _) = await store.ListByJobPagedAsync(jobId, limit: 2, offset: 4, search: null, onlyDiffering: false);
        Assert.Equal("f4.pdf", Assert.Single(page3).RelativePath);
    }

    [Fact]
    public async Task ListByJobPaged_SearchMatchesByName_CaseInsensitive()
    {
        var store = new InMemoryFilePairTaskStore();
        var jobId = Guid.NewGuid();
        await store.CreateManyAsync(
        [
            new FilePairTask { Id = Guid.NewGuid(), JobId = jobId, RelativePath = "Smlouva_A.pdf", OldFilePath = "/o", NewFilePath = "/n" },
            new FilePairTask { Id = Guid.NewGuid(), JobId = jobId, RelativePath = "Dodatek_B.pdf", OldFilePath = "/o", NewFilePath = "/n" },
        ]);

        var (items, total) = await store.ListByJobPagedAsync(jobId, 50, 0, search: "smlouva", onlyDiffering: false);
        Assert.Equal(1, total);
        Assert.Equal("Smlouva_A.pdf", Assert.Single(items).RelativePath);
    }

    [Fact]
    public async Task ListByJobPaged_OnlyDiffering_KeepsDifferingVerdicts_ExcludesIdenticalAndPending()
    {
        var store = new InMemoryFilePairTaskStore();
        var jobId = Guid.NewGuid();
        var identical = new FilePairTask { Id = Guid.NewGuid(), JobId = jobId, RelativePath = "same.pdf", OldFilePath = "/o", NewFilePath = "/n" };
        var differs = new FilePairTask { Id = Guid.NewGuid(), JobId = jobId, RelativePath = "diff.pdf", OldFilePath = "/o", NewFilePath = "/n" };
        var pending = new FilePairTask { Id = Guid.NewGuid(), JobId = jobId, RelativePath = "wait.pdf", OldFilePath = "/o", NewFilePath = "/n" };
        await store.CreateManyAsync([identical, differs, pending]);
        await store.TryClaimAsync(identical.Id, "w", TimeSpan.FromMinutes(5));
        await store.CompleteAsync(identical.Id, new FilePairResult { RelativePath = "same.pdf", Status = FilePairStatus.Identical }, FilePairTaskStatus.Completed);
        await store.TryClaimAsync(differs.Id, "w", TimeSpan.FromMinutes(5));
        await store.CompleteAsync(differs.Id, new FilePairResult { RelativePath = "diff.pdf", Status = FilePairStatus.Differs }, FilePairTaskStatus.Completed);

        var (items, total) = await store.ListByJobPagedAsync(jobId, 50, 0, search: null, onlyDiffering: true);
        Assert.Equal(1, total);
        Assert.Equal("diff.pdf", Assert.Single(items).RelativePath); // identical + pending excluded
    }
}

public class IncrementProcessedTests
{
    [Fact]
    public async Task Increment_IsSequentialAndReturnsTotal()
    {
        var store = new InMemoryJobStore();
        var job = await store.CreateAsync(new ComparisonJob
        {
            Id = Guid.NewGuid(),
            BranchId = Guid.NewGuid(),
            InstanceId = Guid.NewGuid(),
            Request = new BatchComparisonRequest
            {
                Scope = new JobScope("Alfa", "P"),
                OldFolder = "/o",
                NewFolder = "/n",
            },
        });
        await store.SetTotalAsync(job.Id, 3);

        Assert.Equal((1, 3), await store.IncrementProcessedAsync(job.Id));
        Assert.Equal((2, 3), await store.IncrementProcessedAsync(job.Id));
        Assert.Equal((3, 3), await store.IncrementProcessedAsync(job.Id));
    }
}
