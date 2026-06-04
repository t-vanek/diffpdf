using DiffPdf.Core.Abstractions;
using DiffPdf.Core.Models;
using DiffPdf.Messaging.Handlers;
using DiffPdf.Worker;

namespace DiffPdf.Core.Tests;

/// <summary>
/// Part 2 safety net: a single file pair can never wedge or crash a worker — an oversized input is rejected
/// before it is opened, and a comparison that exceeds the wall-clock cap is recorded as a per-file error.
/// </summary>
public class CompareFilePairSafetyTests
{
    private sealed class ThrowingEngine : IComparisonEngine
    {
        public bool Called { get; private set; }
        public Task<FileComparisonResult> CompareAsync(string oldPath, string newPath, ComparisonOptions options, string? artifactDirectory = null, CancellationToken ct = default)
        {
            Called = true;
            throw new InvalidOperationException("engine must not be called");
        }
    }

    private sealed class HangingEngine : IComparisonEngine
    {
        public async Task<FileComparisonResult> CompareAsync(string oldPath, string newPath, ComparisonOptions options, string? artifactDirectory = null, CancellationToken ct = default)
        {
            await Task.Delay(TimeSpan.FromMinutes(5), ct); // never completes before the (zero) timeout fires
            throw new InvalidOperationException("unreachable");
        }
    }

    private sealed class FakePaths : IJobStoragePathProvider
    {
        public string GetJobRoot(ComparisonJob job) => Path.GetTempPath();
        public string GetArtifactsPath(ComparisonJob job) => Path.GetTempPath();
        public string GetReportsPath(ComparisonJob job) => Path.GetTempPath();
        public string GetLogsPath(ComparisonJob job) => Path.GetTempPath();
    }

    private static ComparisonJob Job() => new()
    {
        Id = Guid.NewGuid(),
        BranchId = Guid.NewGuid(),
        InstanceId = Guid.NewGuid(),
        Request = new BatchComparisonRequest
        {
            Scope = new JobScope("Alfa", "Lama"),
            OldFolder = "/old",
            NewFolder = "/new",
            ReportsFolder = "/reports",
        },
    };

    private static FilePairTask Pair(string oldPath, string newPath) => new()
    {
        Id = Guid.NewGuid(),
        JobId = Guid.NewGuid(),
        RelativePath = "doc.pdf",
        OldFilePath = oldPath,
        NewFilePath = newPath,
    };

    [Fact]
    public async Task Oversized_input_is_rejected_as_error_without_opening_it()
    {
        string old = Path.GetTempFileName(), @new = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(old, new byte[1024]);   // 1 KB
            await File.WriteAllBytesAsync(@new, new byte[16]);
            var engine = new ThrowingEngine();
            var options = new WorkerOptions { MaxPdfSizeBytes = 100 }; // limit below the old file

            var result = await CompareFilePairHandler.CompareAsync(
                Pair(old, @new), Job(), engine, new FakePaths(), options, CancellationToken.None);

            Assert.Equal(FilePairStatus.Error, result.Status);
            Assert.Contains("limit", result.Error ?? string.Empty);
            Assert.False(engine.Called); // rejected before the engine (and any file open) is reached
        }
        finally
        {
            File.Delete(old);
            File.Delete(@new);
        }
    }

    [Fact]
    public async Task Comparison_that_exceeds_the_timeout_becomes_a_file_error_not_a_crash()
    {
        string old = Path.GetTempFileName(), @new = Path.GetTempFileName();
        try
        {
            var options = new WorkerOptions { MaxPdfSizeBytes = 0, FilePairComparisonTimeoutMinutes = 0 }; // size check off, instant timeout

            var result = await CompareFilePairHandler.CompareAsync(
                Pair(old, @new), Job(), new HangingEngine(), new FakePaths(), options, CancellationToken.None);

            Assert.Equal(FilePairStatus.Error, result.Status);
            Assert.Contains("limit", result.Error ?? string.Empty);
        }
        finally
        {
            File.Delete(old);
            File.Delete(@new);
        }
    }

    [Fact]
    public async Task Outer_cancellation_propagates_rather_than_becoming_a_file_error()
    {
        string old = Path.GetTempFileName(), @new = Path.GetTempFileName();
        try
        {
            var options = new WorkerOptions { MaxPdfSizeBytes = 0, FilePairComparisonTimeoutMinutes = 10 };
            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                CompareFilePairHandler.CompareAsync(
                    Pair(old, @new), Job(), new HangingEngine(), new FakePaths(), options, cts.Token));
        }
        finally
        {
            File.Delete(old);
            File.Delete(@new);
        }
    }
}
