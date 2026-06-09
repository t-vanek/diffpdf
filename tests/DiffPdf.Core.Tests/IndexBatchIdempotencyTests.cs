using DiffPdf.Core.Abstractions;
using DiffPdf.Core.Models;
using DiffPdf.Messaging.Handlers;
using DiffPdf.Messaging.Messages;
using DiffPdf.Messaging.Observability;
using DiffPdf.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Wolverine;

namespace DiffPdf.Core.Tests;

/// <summary>
/// IndexBatch idempotency: IndexBatch is a durable, at-least-once message, so it can be redelivered for a
/// job that is still Running (the first run committed its tasks but died before acking, or any double
/// dispatch). Indexing again must never mint a SECOND full set of file-pair tasks — that would corrupt the
/// processed/total accounting and double-trigger FinalizeBatch. The redelivery instead re-dispatches the
/// pairs still queued (covering a partial first run) and stops.
/// </summary>
public class IndexBatchIdempotencyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "diffpdf-index-" + Guid.NewGuid().ToString("N"));
    private readonly DiffPdfMetrics _metrics = new();

    private sealed class NoopStorageProvisioner : IStorageProvisioner
    {
        public Task EnsureJobFoldersAsync(ComparisonJob job, CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>Records every message published to the bus; throws on the rest of the IMessageBus surface (unused here).</summary>
    private sealed class RecordingMessageBus : IMessageBus
    {
        public List<object> Published { get; } = [];

        public ValueTask PublishAsync<T>(T message, DeliveryOptions? options = null)
        {
            Published.Add(message!);
            return ValueTask.CompletedTask;
        }

        public string? TenantId { get; set; }
        public ValueTask SendAsync<T>(T message, DeliveryOptions? options = null) => throw new NotImplementedException();
        public ValueTask BroadcastToTopicAsync(string topicName, object message, DeliveryOptions? options = null) => throw new NotImplementedException();
        public IDestinationEndpoint EndpointFor(string endpointName) => throw new NotImplementedException();
        public IDestinationEndpoint EndpointFor(Uri uri) => throw new NotImplementedException();
        public IReadOnlyList<Envelope> PreviewSubscriptions(object message) => throw new NotImplementedException();
        public IReadOnlyList<Envelope> PreviewSubscriptions(object message, DeliveryOptions options) => throw new NotImplementedException();
        public Task InvokeAsync(object message, CancellationToken cancellation = default, TimeSpan? timeout = default) => throw new NotImplementedException();
        public Task InvokeAsync(object message, DeliveryOptions options, CancellationToken cancellation = default, TimeSpan? timeout = default) => throw new NotImplementedException();
        public Task<T> InvokeAsync<T>(object message, CancellationToken cancellation = default, TimeSpan? timeout = default) => throw new NotImplementedException();
        public Task<T> InvokeAsync<T>(object message, DeliveryOptions options, CancellationToken cancellation = default, TimeSpan? timeout = default) => throw new NotImplementedException();
        public Task InvokeForTenantAsync(string tenantId, object message, CancellationToken cancellation = default, TimeSpan? timeout = default) => throw new NotImplementedException();
        public Task<T> InvokeForTenantAsync<T>(string tenantId, object message, CancellationToken cancellation = default, TimeSpan? timeout = default) => throw new NotImplementedException();
        public IAsyncEnumerable<TResponse> StreamAsync<TResponse>(object message, CancellationToken cancellation = default) => throw new NotImplementedException();
        public IAsyncEnumerable<TResponse> StreamAsync<TResponse>(object message, DeliveryOptions options, CancellationToken cancellation = default) => throw new NotImplementedException();
    }

    /// <summary>A Running job whose old/new folders hold <paramref name="pairCount"/> matching PDFs (real, for FolderPairing).</summary>
    private ComparisonJob RunningJob(int pairCount)
    {
        var oldFolder = Path.Combine(_root, "old");
        var newFolder = Path.Combine(_root, "new");
        Directory.CreateDirectory(oldFolder);
        Directory.CreateDirectory(newFolder);
        for (var i = 0; i < pairCount; i++)
        {
            File.WriteAllText(Path.Combine(oldFolder, $"doc{i}.pdf"), "%PDF-1.4");
            File.WriteAllText(Path.Combine(newFolder, $"doc{i}.pdf"), "%PDF-1.4");
        }

        return new ComparisonJob
        {
            Id = Guid.NewGuid(),
            BranchId = Guid.NewGuid(),
            InstanceId = Guid.NewGuid(),
            Status = JobStatus.Running,
            Request = new BatchComparisonRequest
            {
                Scope = new JobScope("Alfa", "Lama"),
                OldFolder = oldFolder,
                NewFolder = newFolder,
                ReportsFolder = Path.Combine(_root, "reports"),
            },
        };
    }

    private Task<BatchFailed?> Index(ComparisonJob job, IJobStore jobStore, IFilePairTaskStore taskStore, IMessageBus bus) =>
        IndexBatchHandler.Handle(
            new IndexBatch(job.Id), jobStore, taskStore, new NoopStorageProvisioner(),
            new NullTriggerEventPublisher(), bus, _metrics,
            NullLogger<IndexBatchHandler>.Instance, CancellationToken.None);

    [Fact]
    public async Task Running_index_twice_yields_exactly_one_set_of_tasks()
    {
        var jobStore = new InMemoryJobStore();
        var taskStore = new InMemoryFilePairTaskStore();
        var bus = new RecordingMessageBus();
        var job = await jobStore.CreateAsync(RunningJob(pairCount: 2));

        var first = await Index(job, jobStore, taskStore, bus);

        Assert.Null(first);
        var afterFirst = await taskStore.ListByJobAsync(job.Id);
        Assert.Equal(2, afterFirst.Count);
        Assert.Equal(2, (await jobStore.GetAsync(job.Id))!.TotalCount);
        Assert.Equal(2, bus.Published.OfType<CompareFilePair>().Count());
        Assert.DoesNotContain(bus.Published, m => m is FinalizeBatch);
        var firstIds = afterFirst.Select(t => t.Id).OrderBy(x => x).ToList();

        // Redelivery: the job is still Running (FinalizeBatch has not completed it), so the handler's status
        // check is not enough — the idempotency guard must catch it.
        var second = await Index(job, jobStore, taskStore, bus);

        Assert.Null(second);
        var afterSecond = await taskStore.ListByJobAsync(job.Id);
        Assert.Equal(2, afterSecond.Count);                                              // one set, not 2N
        Assert.Equal(firstIds, afterSecond.Select(t => t.Id).OrderBy(x => x).ToList());  // same ids — no fresh Guids
        Assert.Equal(2, (await jobStore.GetAsync(job.Id))!.TotalCount);                  // total not doubled
        Assert.DoesNotContain(bus.Published, m => m is FinalizeBatch);                   // pairs still pending → no finalize

        // The still-queued pairs were re-dispatched (so a partial first run still finishes), keyed off the
        // EXISTING task ids — 2 more CompareFilePair, all for ids minted by the first run.
        var dispatched = bus.Published.OfType<CompareFilePair>().ToList();
        Assert.Equal(4, dispatched.Count);
        Assert.All(dispatched, m => Assert.Contains(m.TaskId, firstIds));
    }

    [Fact]
    public async Task Redelivery_after_all_pairs_finished_nudges_finalize_without_new_tasks()
    {
        var jobStore = new InMemoryJobStore();
        var taskStore = new InMemoryFilePairTaskStore();
        var bus = new RecordingMessageBus();
        var job = await jobStore.CreateAsync(RunningJob(pairCount: 2));

        // Index, then drive both pairs to a terminal state while the job stays Running — as if the terminal
        // FinalizeBatch were lost. A redelivered IndexBatch should recover by nudging finalization.
        await Index(job, jobStore, taskStore, bus);
        foreach (var t in await taskStore.ListByJobAsync(job.Id))
        {
            await taskStore.TryClaimAsync(t.Id, "w", TimeSpan.FromMinutes(5));
            await taskStore.CompleteAsync(t.Id, new FilePairResult { RelativePath = t.RelativePath }, FilePairTaskStatus.Completed);
        }
        bus.Published.Clear();

        var result = await Index(job, jobStore, taskStore, bus);

        Assert.Null(result);
        Assert.Equal(2, (await taskStore.ListByJobAsync(job.Id)).Count);     // no new tasks
        Assert.DoesNotContain(bus.Published, m => m is CompareFilePair);     // nothing left to dispatch
        Assert.Single(bus.Published.OfType<FinalizeBatch>());               // finalize nudged (idempotent)
    }

    public void Dispose()
    {
        _metrics.Dispose();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* best effort */ }
    }
}
