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

    /// <summary>Records whether it ran and throws if it does — to prove a dead-lettered pair never reaches the engine.</summary>
    private sealed class ThrowingEngine : IComparisonEngine
    {
        public bool Called { get; private set; }
        public Task<FileComparisonResult> CompareAsync(string oldPath, string newPath, ComparisonOptions options, string? artifactDirectory = null, CancellationToken ct = default)
        {
            Called = true;
            throw new InvalidOperationException("the engine must not run for a dead-lettered (poison) pair");
        }
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

        /// <summary>When set, CompleteAsync reports this without writing — simulates a duplicate that lost the race.</summary>
        public bool? ForceCompleteWon { get; set; }

        public Task<bool> CompleteAsync(Guid taskId, FilePairResult result, FilePairTaskStatus status, CancellationToken ct = default)
        {
            CompleteToken = ct;
            if (ForceCompleteWon is { } won) return Task.FromResult(won);
            return inner.CompleteAsync(taskId, result, status, ct);
        }

        public Task CreateManyAsync(IReadOnlyList<FilePairTask> tasks, CancellationToken ct = default) => inner.CreateManyAsync(tasks, ct);
        public Task<FilePairTask?> TryClaimAsync(Guid taskId, string workerId, TimeSpan lease, CancellationToken ct = default) => inner.TryClaimAsync(taskId, workerId, lease, ct);
        public Task FailAsync(Guid taskId, string error, CancellationToken ct = default) => inner.FailAsync(taskId, error, ct);
        public Task RequeueAsync(Guid taskId, CancellationToken ct = default) => inner.RequeueAsync(taskId, ct);
        public Task RequeueForRetryAsync(Guid taskId, CancellationToken ct = default) => inner.RequeueForRetryAsync(taskId, ct);
        public Task<IReadOnlyList<(Guid JobId, Guid TaskId)>> RequeueStaleAsync(CancellationToken ct = default) => inner.RequeueStaleAsync(ct);
        public Task<IReadOnlyList<(Guid JobId, Guid TaskId)>> RequeueRunningTasksAsync(string? lockedBy, CancellationToken ct = default) => inner.RequeueRunningTasksAsync(lockedBy, ct);
        public Task<IReadOnlyList<(Guid JobId, Guid TaskId)>> ListStaleQueuedAsync(DateTimeOffset idleSince, int limit, CancellationToken ct = default) => inner.ListStaleQueuedAsync(idleSince, limit, ct);
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
    public async Task A_completion_that_lost_the_race_does_not_advance_the_processed_counter()
    {
        var jobStore = new InMemoryJobStore();
        var job = await jobStore.CreateAsync(RunningJob());
        // CompleteAsync reports the pair was already finished by a concurrent/duplicate worker (won == false).
        var taskStore = new TokenCapturingTaskStore(new InMemoryFilePairTaskStore()) { ForceCompleteWon = false };
        var task = QueuedPair(job.Id);
        await taskStore.CreateManyAsync([task]);

        await CompareFilePairHandler.Handle(
            new CompareFilePair(job.Id, task.Id), jobStore, taskStore, new SuccessEngine(), new FakePaths(),
            new NullJobProgressPublisher(), new WorkerInstanceIdProvider(), WorkerOpts(),
            null!, // a lost-race completion returns before any FinalizeBatch publish, so the bus is never touched
            NullLogger<CompareFilePairHandler>.Instance, CancellationToken.None);

        // The losing completion must short-circuit: no double increment, no finalize.
        Assert.Equal(0, (await jobStore.GetAsync(job.Id))!.ProcessedCount);
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

    [Fact]
    public async Task Pair_past_attempt_cap_is_dead_lettered_without_running_the_engine()
    {
        var jobStore = new InMemoryJobStore();
        var job = await jobStore.CreateAsync(RunningJob()); // TotalCount = 2, so finalize is never reached (bus unused)
        var taskStore = new TokenCapturingTaskStore(new InMemoryFilePairTaskStore());
        var task = QueuedPair(job.Id) with { AttemptCount = 3 }; // claim bumps to 4 > MaxFilePairAttempts (3)
        await taskStore.CreateManyAsync([task]);
        var engine = new ThrowingEngine();

        await CompareFilePairHandler.Handle(
            new CompareFilePair(job.Id, task.Id), jobStore, taskStore, engine, new FakePaths(),
            new NullJobProgressPublisher(), new WorkerInstanceIdProvider(), WorkerOpts(),
            null!, NullLogger<CompareFilePairHandler>.Instance, CancellationToken.None);

        Assert.False(engine.Called); // circuit breaker fired before the comparison — no re-crash
        var stored = (await taskStore.ListByJobAsync(job.Id)).Single();
        Assert.Equal(FilePairTaskStatus.Completed, stored.Status);
        Assert.Equal(FilePairStatus.Error, stored.Result!.Status);
        Assert.Contains("poison pill", stored.Result!.Error);
        Assert.Equal(1, (await jobStore.GetAsync(job.Id))!.ProcessedCount); // dead-letter still advances the batch
    }

    [Fact]
    public async Task Pair_at_attempt_cap_still_runs_the_comparison()
    {
        var jobStore = new InMemoryJobStore();
        var job = await jobStore.CreateAsync(RunningJob());
        var taskStore = new TokenCapturingTaskStore(new InMemoryFilePairTaskStore());
        var task = QueuedPair(job.Id) with { AttemptCount = 2 }; // claim bumps to 3, NOT > 3 → compares normally
        await taskStore.CreateManyAsync([task]);

        await CompareFilePairHandler.Handle(
            new CompareFilePair(job.Id, task.Id), jobStore, taskStore, new SuccessEngine(), new FakePaths(),
            new NullJobProgressPublisher(), new WorkerInstanceIdProvider(), WorkerOpts(),
            null!, NullLogger<CompareFilePairHandler>.Instance, CancellationToken.None);

        var stored = (await taskStore.ListByJobAsync(job.Id)).Single();
        Assert.Equal(FilePairTaskStatus.Completed, stored.Status);
        Assert.Null(stored.Result!.Error); // compared, not dead-lettered (which would set a poison Error)
    }
}
