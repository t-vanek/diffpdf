using DiffPdf.Core.Abstractions;
using DiffPdf.Core.Models;
using Microsoft.Extensions.Options;

namespace DiffPdf.Core.Storage;

/// <summary>
/// Builds job storage paths as <c>{root}/{businessInstanceKey}/{projectKey}/jobs/{jobId}/...</c>.
/// Keys are validated and the final path is checked to stay under the root.
/// </summary>
public sealed class FileSystemStoragePathProvider(IOptions<StorageOptions> options) : IJobStoragePathProvider
{
    private readonly StorageOptions _options = options.Value;

    public string GetJobRoot(ComparisonJob job)
    {
        StorageKeyValidator.EnsureValid(job.BusinessInstanceKey, nameof(job.BusinessInstanceKey));
        StorageKeyValidator.EnsureValid(job.ProjectKey, nameof(job.ProjectKey));

        string root = Path.GetFullPath(_options.RootPath);
        string jobRoot = Path.GetFullPath(Path.Combine(
            root, job.BusinessInstanceKey, job.ProjectKey, "jobs", job.Id.ToString("N")));

        // Defence in depth: the validated keys already prevent traversal.
        if (!jobRoot.StartsWith(root, StringComparison.Ordinal))
            throw new InvalidOperationException("Resolved job path escapes the storage root.");

        return jobRoot;
    }

    public string GetArtifactsPath(ComparisonJob job) => Path.Combine(GetJobRoot(job), "artifacts");
    public string GetReportsPath(ComparisonJob job) => Path.Combine(GetJobRoot(job), "reports");
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

        Directory.CreateDirectory(paths.GetArtifactsPath(job));
        Directory.CreateDirectory(paths.GetReportsPath(job));
        Directory.CreateDirectory(paths.GetLogsPath(job));
        return Task.CompletedTask;
    }
}
