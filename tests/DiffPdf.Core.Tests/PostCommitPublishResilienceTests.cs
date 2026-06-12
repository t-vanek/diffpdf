using DiffPdf.Application.Abstractions;
using DiffPdf.Core.Abstractions;
using DiffPdf.Core.Models;
using DiffPdf.Messaging.Handlers;
using DiffPdf.Messaging.Messages;
using DiffPdf.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiffPdf.Core.Tests;

/// <summary>
/// Regression guard for the post-commit publish hardening: once a job's terminal state is committed,
/// a throwing realtime publisher (SignalR) must never abort the handler — a throw would make Wolverine
/// redeliver, the retry would see a non-Running job and return null, and the BatchFinished/BatchFailed
/// cascade (e-mail notifications, queue advance, event-triggered automations) would be lost forever.
/// </summary>
public class PostCommitPublishResilienceTests
{
    private sealed class ThrowingProgressPublisher : IJobProgressPublisher
    {
        public Task PublishAsync(JobProgressChanged progress, CancellationToken ct = default) =>
            throw new InvalidOperationException("signalr down");
    }

    private sealed class ThrowingTriggerEventPublisher : ITriggerEventPublisher
    {
        public Task PublishAsync(TriggerEvent evt, CancellationToken ct = default) =>
            throw new InvalidOperationException("signalr down");
    }

    private sealed class NoopProvisioner : IStorageProvisioner
    {
        public Task EnsureJobFoldersAsync(ComparisonJob job, CancellationToken ct = default) => Task.CompletedTask;
    }

    private static ComparisonJob RunningJob(Guid? sourceAutomationId = null, string oldFolder = "/old", string newFolder = "/new") => new()
    {
        Id = Guid.NewGuid(),
        BranchId = Guid.NewGuid(),
        InstanceId = Guid.NewGuid(),
        Status = JobStatus.Running,
        TriggerId = Guid.NewGuid(), // exercises the trigger-event publish path
        SourceAutomationId = sourceAutomationId,
        Request = new BatchComparisonRequest
        {
            Scope = new JobScope("Alfa", "Lama"),
            OldFolder = oldFolder,
            NewFolder = newFolder,
            ReportsFolder = "/reports",
        },
    };

    [Fact]
    public async Task Finalize_WithThrowingPublishers_StillReturnsBatchFinished_AndCompletesTheJob()
    {
        var jobs = new InMemoryJobStore();
        var tasks = new InMemoryFilePairTaskStore();
        var launcher = Guid.NewGuid();
        var job = RunningJob(sourceAutomationId: launcher);
        await jobs.CreateAsync(job);
        using var metrics = new DiffPdfMetrics();

        var result = await FinalizeBatchHandler.Handle(
            new FinalizeBatch(job.Id), jobs, tasks,
            new ThrowingProgressPublisher(), new ThrowingTriggerEventPublisher(),
            new NullSystemEventLog(), metrics, NullLogger<FinalizeBatchHandler>.Instance, CancellationToken.None);

        Assert.NotNull(result); // the cascade survives the realtime outage
        Assert.Equal(job.Id, result!.JobId);
        Assert.Equal(launcher, result.SourceAutomationId);
        Assert.Equal(JobStatus.Completed, (await jobs.GetAsync(job.Id))!.Status);
    }

    [Fact]
    public async Task IndexFailure_WithThrowingPublisher_StillReturnsBatchFailed_AndFailsTheJob()
    {
        var jobs = new InMemoryJobStore();
        var tasks = new InMemoryFilePairTaskStore();
        var launcher = Guid.NewGuid();
        // A folder that cannot exist → FolderPairing.Pair throws → the indexing-failure path runs.
        var missing = Path.Combine(Path.GetTempPath(), "diffpdf-neexistuje", Guid.NewGuid().ToString("N"));
        var job = RunningJob(sourceAutomationId: launcher, oldFolder: missing, newFolder: missing);
        await jobs.CreateAsync(job);
        using var metrics = new DiffPdfMetrics();

        var result = await IndexBatchHandler.Handle(
            new IndexBatch(job.Id), jobs, tasks, new NoopProvisioner(),
            new ThrowingTriggerEventPublisher(), new NullSystemEventLog(), new RecordingMessageBus(),
            metrics, NullLogger<IndexBatchHandler>.Instance, CancellationToken.None);

        Assert.NotNull(result); // the Failed notification cascade survives the realtime outage
        Assert.Equal(job.Id, result!.JobId);
        Assert.Equal(launcher, result.SourceAutomationId);
        Assert.Equal(JobStatus.Failed, (await jobs.GetAsync(job.Id))!.Status);
    }

    [Fact]
    public async Task Finalize_WithHealthyPublishers_AlsoReturnsBatchFinished()
    {
        // Sanity companion: proves the throwing-publisher tests do not pass vacuously.
        var jobs = new InMemoryJobStore();
        var tasks = new InMemoryFilePairTaskStore();
        var job = RunningJob();
        await jobs.CreateAsync(job);
        using var metrics = new DiffPdfMetrics();

        var result = await FinalizeBatchHandler.Handle(
            new FinalizeBatch(job.Id), jobs, tasks,
            new NullJobProgressPublisher(), new NullTriggerEventPublisher(),
            new NullSystemEventLog(), metrics, NullLogger<FinalizeBatchHandler>.Instance, CancellationToken.None);

        Assert.NotNull(result);
    }
}
