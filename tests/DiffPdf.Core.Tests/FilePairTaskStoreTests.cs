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
            BusinessInstanceId = Guid.NewGuid(),
            ProjectId = Guid.NewGuid(),
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
