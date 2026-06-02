using System.Collections.Concurrent;
using DiffPdf.Core.Abstractions;
using DiffPdf.Core.Models;
using Microsoft.Extensions.Logging;

namespace DiffPdf.Core.Comparison;

/// <summary>
/// Walks the old and new folders, pairs files by relative path, and compares
/// each pair in parallel (bounded). Unmatched files are reported as
/// only-in-old / only-in-new.
/// </summary>
public sealed class BatchComparer(IComparisonEngine engine, ILogger<BatchComparer> logger) : IBatchComparer
{
    public async Task<BatchComparisonReport> CompareAsync(
        BatchComparisonRequest request,
        string artifactDirectory,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        var startedAt = DateTimeOffset.UtcNow;

        var oldFiles = Index(request.OldFolder, request.SearchPattern, request.Recursive);
        var newFiles = Index(request.NewFolder, request.SearchPattern, request.Recursive);
        var allKeys = oldFiles.Keys.Union(newFiles.Keys).OrderBy(k => k).ToList();

        var results = new ConcurrentBag<FilePairResult>();
        int processed = 0;

        int dop = request.MaxDegreeOfParallelism > 0
            ? request.MaxDegreeOfParallelism
            : Environment.ProcessorCount;

        await Parallel.ForEachAsync(
            allKeys,
            new ParallelOptions { MaxDegreeOfParallelism = dop, CancellationToken = ct },
            async (key, token) =>
            {
                bool inOld = oldFiles.TryGetValue(key, out var oldPath);
                bool inNew = newFiles.TryGetValue(key, out var newPath);

                FilePairResult result;
                if (inOld && !inNew)
                {
                    result = new FilePairResult { RelativePath = key, Status = FilePairStatus.OnlyInOld };
                }
                else if (!inOld && inNew)
                {
                    result = new FilePairResult { RelativePath = key, Status = FilePairStatus.OnlyInNew };
                }
                else
                {
                    result = await CompareOneAsync(key, oldPath!, newPath!, request, artifactDirectory, token);
                }

                results.Add(result);
                int done = Interlocked.Increment(ref processed);
                progress?.Report(done);
            });

        return new BatchComparisonReport
        {
            OldFolder = request.OldFolder,
            NewFolder = request.NewFolder,
            StartedAt = startedAt,
            CompletedAt = DateTimeOffset.UtcNow,
            Files = results.OrderBy(r => r.RelativePath).ToList(),
            Gate = request.Gate,
        };
    }

    private async Task<FilePairResult> CompareOneAsync(
        string key, string oldPath, string newPath,
        BatchComparisonRequest request, string artifactDirectory, CancellationToken ct)
    {
        try
        {
            string pairArtifactDir = Path.Combine(artifactDirectory, Path.GetDirectoryName(key) ?? string.Empty);
            var fileResult = await engine.CompareAsync(oldPath, newPath, request.Options, pairArtifactDir, ct);

            if (fileResult.Outcome == ComparisonOutcome.Failed)
            {
                return new FilePairResult
                {
                    RelativePath = key,
                    Status = FilePairStatus.Error,
                    Error = fileResult.Error,
                };
            }

            return new FilePairResult
            {
                RelativePath = key,
                Status = fileResult.AreIdentical ? FilePairStatus.Identical : FilePairStatus.Differs,
                Similarity = fileResult.Similarity,
                DifferingPages = fileResult.DifferingPages,
                ContentErrorCount = fileResult.ContentErrors.Count,
                HighlightedPdfPath = fileResult.HighlightedPdfPath,
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Comparison failed for {Key}", key);
            return new FilePairResult { RelativePath = key, Status = FilePairStatus.Error, Error = ex.Message };
        }
    }

    private static Dictionary<string, string> Index(string root, string pattern, bool recursive)
    {
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Folder not found: {root}");

        var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        return Directory.EnumerateFiles(root, pattern, option)
            .ToDictionary(
                f => Path.GetRelativePath(root, f).Replace('\\', '/'),
                f => f,
                StringComparer.OrdinalIgnoreCase);
    }
}
