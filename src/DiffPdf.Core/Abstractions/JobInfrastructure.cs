using DiffPdf.Core.Models;

namespace DiffPdf.Core.Abstractions;

/// <summary>Realtime job progress event (also used as the SignalR payload).</summary>
public sealed record JobProgressChanged(
    Guid JobId,
    string BusinessInstanceKey,
    string ProjectKey,
    string Status,
    int ProcessedCount,
    int TotalCount,
    double Progress,
    string? Error = null)
{
    public static JobProgressChanged From(ComparisonJob job) => new(
        job.Id, job.BusinessInstanceKey, job.ProjectKey, job.Status.ToString(),
        job.ProcessedCount, job.TotalCount, job.Progress, job.Error);
}

/// <summary>Pushes job progress to interested clients (SignalR). A no-op is acceptable.</summary>
public interface IJobProgressPublisher
{
    Task PublishAsync(JobProgressChanged progress, CancellationToken ct = default);
}

/// <summary>Default no-op publisher used until a realtime transport (SignalR) is wired in.</summary>
public sealed class NullJobProgressPublisher : IJobProgressPublisher
{
    public Task PublishAsync(JobProgressChanged progress, CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>Resolves the on-disk locations for a job's input/output, scoped by business instance + project.</summary>
public interface IJobStoragePathProvider
{
    string GetJobRoot(ComparisonJob job);
    string GetArtifactsPath(ComparisonJob job);
    string GetReportsPath(ComparisonJob job);
    string GetLogsPath(ComparisonJob job);
}

/// <summary>Creates the storage folders for instances / projects / jobs on demand.</summary>
public interface IStorageProvisioner
{
    Task EnsureJobFoldersAsync(ComparisonJob job, CancellationToken ct = default);
}

/// <summary>Identifies the running worker process (for job lease ownership).</summary>
public interface IWorkerInstanceIdProvider
{
    string WorkerInstanceId { get; }
}

/// <summary>Machine + process based worker id.</summary>
public sealed class WorkerInstanceIdProvider : IWorkerInstanceIdProvider
{
    public string WorkerInstanceId { get; } =
        $"{Environment.MachineName}:{Environment.ProcessId}";
}
