using DiffPdf.Core.Abstractions;
using DiffPdf.Core.Models;

namespace DiffPdf.Core.Tests;

/// <summary>Records the file pairs requeued for retry, for assertions (replaces the raw message bus).</summary>
internal sealed class CapturingRequeueDispatcher : IFilePairRequeueDispatcher
{
    public List<(Guid JobId, Guid TaskId)> Requeued { get; } = [];

    public Task RequeueAsync(Guid jobId, Guid taskId, CancellationToken ct = default)
    {
        Requeued.Add((jobId, taskId));
        return Task.CompletedTask;
    }
}

/// <summary>No-op realtime job-progress publisher.</summary>
internal sealed class NoopJobProgressPublisher : IJobProgressPublisher
{
    public Task PublishAsync(JobProgressChanged progress, CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>Returns a fixed resume result (default: not paused, nothing re-dispatched).</summary>
internal sealed class StubJobResumeService(ComparisonJob? job = null, int redispatched = 0) : IJobResumeService
{
    public Task<(ComparisonJob? Job, int Redispatched)> ResumeAsync(Guid jobId, CancellationToken ct = default) =>
        Task.FromResult((job, redispatched));
}

/// <summary>Maps every storage path to a single fixed root, so the artifact-path guard can be exercised on disk.</summary>
internal sealed class FixedJobStoragePathProvider(string root) : IJobStoragePathProvider
{
    public string GetJobRoot(ComparisonJob job) => root;
    public string GetArtifactsPath(ComparisonJob job) => root;
    public string GetReportsPath(ComparisonJob job) => root;
    public string GetLogsPath(ComparisonJob job) => root;
}
