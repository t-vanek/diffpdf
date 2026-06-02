using DiffPdf.Core.Abstractions;
using DiffPdf.Core.Models;
using Microsoft.Extensions.Options;

namespace DiffPdf.Core.Storage;

/// <summary>
/// Builds per-job storage paths under the instance's resolved reports folder:
/// <c>{ReportsFolder}/{jobId}/...</c>. <see cref="ComparisonInstance.BasePath"/> is
/// resolved server-side at submit time, so the job already carries a concrete
/// <c>ReportsFolder</c> (= <c>{base}/reports</c>).
/// </summary>
public sealed class FileSystemStoragePathProvider : IJobStoragePathProvider
{
    public string GetJobRoot(ComparisonJob job)
    {
        string reportsFolder = job.Request.ReportsFolder;
        if (string.IsNullOrWhiteSpace(reportsFolder))
            throw new InvalidOperationException(
                $"Job {job.Id} has no resolved ReportsFolder; it must be derived from the instance base path at submit time.");

        // The job id is a GUID in 'N' format — path-safe and cannot traverse out of the reports folder.
        return Path.Combine(reportsFolder, job.Id.ToString("N"));
    }

    public string GetArtifactsPath(ComparisonJob job) => Path.Combine(GetJobRoot(job), "artifacts");

    /// <summary>The job's own folder under the instance's <c>reports/</c> area; the JSON report lives here.</summary>
    public string GetReportsPath(ComparisonJob job) => GetJobRoot(job);

    public string GetLogsPath(ComparisonJob job) => Path.Combine(GetJobRoot(job), "logs");
}

/// <summary>Creates job folders on disk.</summary>
public sealed class FileSystemStorageProvisioner(
    IJobStoragePathProvider paths,
    IOptions<StorageOptions> options) : IStorageProvisioner
{
    private readonly StorageOptions _options = options.Value;

    public Task EnsureJobFoldersAsync(ComparisonJob job, CancellationToken ct = default)
    {
        if (!_options.CreateFoldersOnDemand) return Task.CompletedTask;

        Directory.CreateDirectory(paths.GetReportsPath(job));
        Directory.CreateDirectory(paths.GetArtifactsPath(job));
        Directory.CreateDirectory(paths.GetLogsPath(job));
        return Task.CompletedTask;
    }
}
