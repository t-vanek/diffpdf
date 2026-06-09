using System.Diagnostics;
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
/// The per-pair comparison emits a "comparison.pair" span on the <c>DiffPdf.Comparison</c> source, carrying the
/// domain tags (job id, pair, outcome) that let a job read as one trace under an OTLP exporter.
/// </summary>
public class ComparisonTracingTests
{
    private sealed class SuccessEngine : IComparisonEngine
    {
        public Task<FileComparisonResult> CompareAsync(string oldPath, string newPath, ComparisonOptions options, string? artifactDirectory = null, CancellationToken ct = default) =>
            Task.FromResult(new FileComparisonResult { OldPath = oldPath, NewPath = newPath });
    }
    private sealed class FakePaths : IJobStoragePathProvider
    {
        public string GetJobRoot(ComparisonJob job) => Path.GetTempPath();
        public string GetArtifactsPath(ComparisonJob job) => Path.GetTempPath();
        public string GetReportsPath(ComparisonJob job) => Path.GetTempPath();
        public string GetLogsPath(ComparisonJob job) => Path.GetTempPath();
    }

    [Fact]
    public async Task CompareFilePair_emits_a_comparison_pair_span_with_job_and_pair_tags()
    {
        var captured = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "DiffPdf.Comparison",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = a => { lock (captured) captured.Add(a); }, // thread-safe: concurrent tests' spans hit this global listener
        };
        ActivitySource.AddActivityListener(listener);

        var jobStore = new InMemoryJobStore();
        var job = await jobStore.CreateAsync(new ComparisonJob
        {
            Id = Guid.NewGuid(),
            BranchId = Guid.NewGuid(),
            InstanceId = Guid.NewGuid(),
            Status = JobStatus.Running,
            TotalCount = 2, // 2 pairs, so this single pair is not the last → FinalizeBatch is never published (bus unused)
            Request = new BatchComparisonRequest
            {
                Scope = new JobScope("Alfa", "Lama"),
                OldFolder = "/old",
                NewFolder = "/new",
                ReportsFolder = "/reports",
            },
        });
        var taskStore = new InMemoryFilePairTaskStore();
        var task = new FilePairTask
        {
            Id = Guid.NewGuid(),
            JobId = job.Id,
            RelativePath = "doc.pdf",
            OldFilePath = "/old/doc.pdf",
            NewFilePath = "/new/doc.pdf",
        };
        await taskStore.CreateManyAsync([task]);

        await CompareFilePairHandler.Handle(
            new CompareFilePair(job.Id, task.Id), jobStore, taskStore, new SuccessEngine(), new FakePaths(),
            new NullJobProgressPublisher(), new WorkerInstanceIdProvider(), Options.Create(new WorkerOptions { MaxPdfSizeBytes = 0 }),
            null!, NullLogger<CompareFilePairHandler>.Instance, CancellationToken.None);

        // Filter to THIS test's job: the listener is process-global, so a comparison.pair span from another test's
        // handler running in parallel would otherwise pollute the count (xUnit runs classes concurrently). Snapshot
        // under the lock first so a concurrent add never races the enumeration.
        List<Activity> snapshot;
        lock (captured) snapshot = captured.ToList();
        var span = Assert.Single(snapshot, a => a.OperationName == "comparison.pair"
            && a.GetTagItem("diffpdf.job_id")?.ToString() == job.Id.ToString());
        Assert.Equal(job.Id.ToString(), span.GetTagItem("diffpdf.job_id")?.ToString());
        Assert.Equal("doc.pdf", span.GetTagItem("diffpdf.pair"));
        Assert.NotNull(span.GetTagItem("diffpdf.outcome")); // outcome tagged after the compare
    }
}
