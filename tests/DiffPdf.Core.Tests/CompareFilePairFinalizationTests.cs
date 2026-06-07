using DiffPdf.Core.Abstractions;
using DiffPdf.Core.Models;
using DiffPdf.Messaging.Handlers;
using DiffPdf.Messaging.Messages;
using DiffPdf.Persistence;
using DiffPdf.Worker;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DiffPdf.Core.Tests;

/// <summary>
/// Cancellation semantics of the file-pair worker (CompareFilePairHandler): a host shutdown must never
/// (a) strand a finished pair by abandoning the completion write to the message token, nor (b) record a
/// pair interrupted mid-compare as a spurious permanent error.
/// </summary>
public class CompareFilePairFinalizationTests
{
    private sealed class SuccessEngine : IComparisonEngine
    {
        public Task<FileComparisonResult> CompareAsync(string oldPath, string newPath, ComparisonOptions options, string? artifactDirectory = null, CancellationToken ct = default) =>
            Task.FromResult(new FileComparisonResult { OldPath = oldPath, NewPath = newPath }); // Compared, identical
    }

    private sealed class FakePaths : IJobStoragePathProvider
    {
        public string GetJobRoot(ComparisonJob job) => Path.GetTempPath();
        public string GetArtifactsPath(ComparisonJob job) => Path.GetTempPath();
        public string GetReportsPath(ComparisonJob job) => Path.GetTempPath();
        public string GetLogsPath(ComparisonJob job) => Path.GetTempPath();
    }

    /// <summary>Records the token Handle hands to CompleteAsync; forwards everything else to a real in-memory store.</summary>
    private sealed class TokenCapturingTaskStore(InMemoryFilePairTaskStore inner) : IFilePairTaskStore
    {
        public CancellationToken? CompleteToken { get; private set; }

        public Task CompleteAsync(Guid taskId, FilePairResult result, FilePairTaskStatus status, CancellationToken ct = default)
        {
            CompleteToken = ct;
            return inner.CompleteAsync(taskId, result, status, ct);
        }

        public Task CreateManyAsync(IReadOnlyList<FilePairTask> tasks, CancellationToken ct = default) => inner.CreateManyAsync(tasks, ct);
        public Task<FilePairTask?> TryClaimAsync(Guid taskId, string workerId, TimeSpan lease, CancellationToken ct = default) => inner.TryClaimAsync(taskId, workerId, lease, ct);
        public Task FailAsync(Guid taskId, string error, CancellationToken ct = default) => inner.FailAsync(taskId, error, ct);
        public Task RequeueAsync(Guid taskId, CancellationToken ct = default) => inner.RequeueAsync(taskId, ct);
        public Task RequeueForRetryAsync(Guid taskId, CancellationToken ct = default) => inner.RequeueForRetryAsync(taskId, ct);
        public Task<IReadOnlyList<(Guid JobId, Guid TaskId)>> RequeueStaleAsync(CancellationToken ct = default) => inner.RequeueStaleAsync(ct);
        public Task<IReadOnlyList<FilePairTask>> ListByJobAsync(Guid jobId, CancellationToken ct = default) => inner.ListByJobAsync(jobId, ct);
        public Task<int> CountActiveAsync(CancellationToken ct = default) => inner.CountActiveAsync(ct);
        public Task<IReadOnlyDictionary<FilePairTaskStatus, int>> CountByStatusForJobsAsync(IReadOnlyCollection<Guid> jobIds, CancellationToken ct = default) => inner.CountByStatusForJobsAsync(jobIds, ct);
        public Task<int> DeleteForJobsAsync(IReadOnlyCollection<Guid> jobIds, CancellationToken ct = default) => inner.DeleteForJobsAsync(jobIds, ct);
        public Task<int> SkipPendingForJobAsync(Guid jobId, CancellationToken ct = default) => inner.SkipPendingForJobAsync(jobId, ct);
        public Task<int> SkipPendingForTerminalJobsAsync(CancellationToken ct = default) => inner.SkipPendingForTerminalJobsAsync(ct);
    }

    private static ComparisonJob RunningJob() => new()
    {
        Id = Guid.NewGuid(),
        BranchId = Guid.NewGuid(),
        InstanceId = Guid.NewGuid(),
        Status = JobStatus.Running,
        TotalCount = 2, // 2 pairs, so a single completed pair is not the last → FinalizeBatch is never published (bus unused)
        Request = new BatchComparisonRequest
        {
            Scope = new JobScope("Alfa", "Lama"),
            OldFolder = "/old",
            NewFolder = "/new",
            ReportsFolder = "/reports",
        },
    };

    private static FilePairTask QueuedPair(Guid jobId) => new()
    {
        Id = Guid.NewGuid(),
        JobId = jobId,
        RelativePath = "doc.pdf",
        OldFilePath = "/old/doc.pdf",
        NewFilePath = "/new/doc.pdf",
    };

    private static IOptions<WorkerOptions> WorkerOpts() =>
        Options.Create(new WorkerOptions { MaxPdfSizeBytes = 0, FilePairComparisonTimeoutMinutes = 10 });

    [Fact]
    public async Task Finalization_records_the_pair_with_a_non_cancellable_token()
    {
        var jobStore = new InMemoryJobStore();
        var job = await jobStore.CreateAsync(RunningJob());
        var taskStore = new TokenCapturingTaskStore(new InMemoryFilePairTaskStore());
        var task = QueuedPair(job.Id);
        await taskStore.CreateManyAsync([task]);

        // A token that *can* be cancelled (like the real Wolverine message token) but is not cancelled here.
        using var cts = new CancellationTokenSource();

        await CompareFilePairHandler.Handle(
            new CompareFilePair(job.Id, task.Id), jobStore, taskStore, new SuccessEngine(), new FakePaths(),
            new NullJobProgressPublisher(), new WorkerInstanceIdProvider(), WorkerOpts(),
            null!, // IMessageBus is only used to publish FinalizeBatch on the last pair; total=2 so it is never reached
            NullLogger<CompareFilePairHandler>.Instance, cts.Token);

        // The completion write must not ride the cancellable message token, or a shutdown mid-finalize would
        // strand the task. CancellationToken.None reports CanBeCanceled == false.
        Assert.True(taskStore.CompleteToken.HasValue);
        Assert.False(taskStore.CompleteToken!.Value.CanBeCanceled);

        var stored = (await taskStore.ListByJobAsync(job.Id)).Single();
        Assert.Equal(FilePairTaskStatus.Completed, stored.Status);
        Assert.Equal(1, (await jobStore.GetAsync(job.Id))!.ProcessedCount);
    }

    [Fact]
    public async Task Shutdown_during_compare_propagates_and_does_not_record_a_spurious_error()
    {
        var jobStore = new InMemoryJobStore();
        var job = await jobStore.CreateAsync(RunningJob());
        var taskStore = new InMemoryFilePairTaskStore();
        var task = QueuedPair(job.Id);
        await taskStore.CreateManyAsync([task]);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync(); // host shutting down before/while the pair is compared

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CompareFilePairHandler.Handle(
                new CompareFilePair(job.Id, task.Id), jobStore, taskStore, new SuccessEngine(), new FakePaths(),
                new NullJobProgressPublisher(), new WorkerInstanceIdProvider(), WorkerOpts(),
                null!, NullLogger<CompareFilePairHandler>.Instance, cts.Token));

        // Cancellation is a shutdown, not a pair failure: the pair must NOT be finalized (Completed/Failed);
        // it stays claimed (Running) so the lease sweeper re-queues it for a retry after restart.
        var stored = (await taskStore.ListByJobAsync(job.Id)).Single();
        Assert.NotEqual(FilePairTaskStatus.Completed, stored.Status);
        Assert.NotEqual(FilePairTaskStatus.Failed, stored.Status);
        Assert.Equal(0, (await jobStore.GetAsync(job.Id))!.ProcessedCount);
    }
}
