using DiffPdf.Application.Jobs;
using DiffPdf.Core.Models;
using DiffPdf.Persistence;

namespace DiffPdf.Core.Tests;

/// <summary>Unit coverage for the job lifecycle + artifact logic moved out of the /jobs endpoints into JobService.</summary>
public class JobServiceTests
{
    private sealed record Ctx(JobService Svc, InMemoryJobStore Jobs, InMemoryFilePairTaskStore Tasks, CapturingRequeueDispatcher Requeue);

    private static Ctx Build(string? artifactsRoot = null)
    {
        var jobs = new InMemoryJobStore();
        var tasks = new InMemoryFilePairTaskStore();
        var requeue = new CapturingRequeueDispatcher();
        var svc = new JobService(
            jobs, tasks, new NoopBranchQueueDispatcher(), new NoopJobProgressPublisher(),
            new StubJobResumeService(), requeue, new FixedJobStoragePathProvider(artifactsRoot ?? Path.GetTempPath()));
        return new Ctx(svc, jobs, tasks, requeue);
    }

    private static ComparisonJob MakeJob(JobStatus status) => new()
    {
        Id = Guid.NewGuid(),
        BranchId = Guid.NewGuid(),
        InstanceId = Guid.NewGuid(),
        Status = status,
        Request = new BatchComparisonRequest
        {
            Scope = new JobScope("Alfa", "Lama"),
            OldFolder = "/old",
            NewFolder = "/new",
            ReportsFolder = "/reports",
        },
    };

    [Fact]
    public async Task Cancel_UnknownJob_ReturnsNull() => Assert.Null(await Build().Svc.CancelAsync(Guid.NewGuid()));

    [Fact]
    public async Task Pause_UnknownJob_ReturnsNull() => Assert.Null(await Build().Svc.PauseAsync(Guid.NewGuid()));

    [Fact]
    public async Task Resume_UnknownJob_ReturnsNull() => Assert.Null(await Build().Svc.ResumeAsync(Guid.NewGuid()));

    [Fact]
    public async Task Retry_UnknownJob_ReturnsNull() => Assert.Null(await Build().Svc.RetryAsync(Guid.NewGuid()));

    [Fact]
    public async Task Retry_UnfinishedJob_ThrowsConflict()
    {
        var c = Build();
        var job = MakeJob(JobStatus.Running);
        await c.Jobs.CreateAsync(job);
        await Assert.ThrowsAsync<JobConflictException>(() => c.Svc.RetryAsync(job.Id));
    }

    [Fact]
    public async Task Retry_FinishedWithNoFailedTasks_ReturnsZero_AndPublishesNothing()
    {
        var c = Build();
        var job = MakeJob(JobStatus.Completed);
        await c.Jobs.CreateAsync(job);

        var outcome = await c.Svc.RetryAsync(job.Id);

        Assert.NotNull(outcome);
        Assert.Equal(0, outcome!.Retried);
        Assert.Empty(c.Requeue.Requeued);
    }

    [Fact]
    public async Task Delete_UnknownJob_ReturnsFalse() => Assert.False(await Build().Svc.DeleteAsync(Guid.NewGuid()));

    [Theory]
    [InlineData(JobStatus.Draft)]
    [InlineData(JobStatus.Queued)]
    [InlineData(JobStatus.Running)]
    [InlineData(JobStatus.Paused)]
    [InlineData(JobStatus.Failed)] // per spec only Hotovo/Zrušeno is deletable — a Failed job is retried or cancelled first
    public async Task Delete_NotDeletableStatus_ThrowsConflict_AndKeepsTheJob(JobStatus status)
    {
        var c = Build();
        var job = MakeJob(status);
        await c.Jobs.CreateAsync(job);

        await Assert.ThrowsAsync<JobConflictException>(() => c.Svc.DeleteAsync(job.Id));
        Assert.NotNull(await c.Jobs.GetAsync(job.Id));
    }

    [Theory]
    [InlineData(JobStatus.Completed)]
    [InlineData(JobStatus.Cancelled)]
    public async Task Delete_FinishedJob_RemovesRowTasksAndDiskFolder(JobStatus status)
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        await File.WriteAllTextAsync(Path.Combine(root, "diff.pdf"), "%PDF-1.4");
        var c = Build(root);
        var job = MakeJob(status);
        await c.Jobs.CreateAsync(job);
        await c.Tasks.CreateManyAsync([new FilePairTask
        {
            Id = Guid.NewGuid(), JobId = job.Id, RelativePath = "doc.pdf", Status = FilePairTaskStatus.Completed,
        }]);

        Assert.True(await c.Svc.DeleteAsync(job.Id));

        Assert.Null(await c.Jobs.GetAsync(job.Id));
        Assert.Empty(await c.Tasks.ListByJobAsync(job.Id));
        Assert.False(Directory.Exists(root)); // the on-disk job folder (report + artifacts) went with it
    }

    [Fact]
    public async Task ResolveArtifact_UnknownJob_ReturnsJobNotFound()
    {
        var r = await Build().Svc.ResolveArtifactAsync(Guid.NewGuid(), "file.pdf");
        Assert.Equal(ArtifactOutcome.JobNotFound, r.Outcome);
    }

    [Fact]
    public async Task ResolveArtifact_PathTraversal_ReturnsInvalidPath()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        var c = Build(root);
        var job = MakeJob(JobStatus.Completed);
        await c.Jobs.CreateAsync(job);

        var r = await c.Svc.ResolveArtifactAsync(job.Id, "../../escape.pdf");
        Assert.Equal(ArtifactOutcome.InvalidPath, r.Outcome);
    }

    [Fact]
    public async Task ResolveArtifact_MissingFile_ReturnsFileNotFound()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        var c = Build(root);
        var job = MakeJob(JobStatus.Completed);
        await c.Jobs.CreateAsync(job);

        var r = await c.Svc.ResolveArtifactAsync(job.Id, "missing.pdf");
        Assert.Equal(ArtifactOutcome.FileNotFound, r.Outcome);
    }

    [Fact]
    public async Task ResolveArtifact_ExistingFile_ReturnsOk()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        await File.WriteAllTextAsync(Path.Combine(root, "report.pdf"), "%PDF-1.4");
        var c = Build(root);
        var job = MakeJob(JobStatus.Completed);
        await c.Jobs.CreateAsync(job);

        var r = await c.Svc.ResolveArtifactAsync(job.Id, "report.pdf");
        Assert.Equal(ArtifactOutcome.Ok, r.Outcome);
        Assert.EndsWith("report.pdf", r.AbsolutePath);
    }
}
