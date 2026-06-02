namespace DiffPdf.Core.Models;

/// <summary>Request to compare every PDF under <see cref="OldFolder"/> against <see cref="NewFolder"/>.</summary>
public sealed record BatchComparisonRequest
{
    public required string OldFolder { get; init; }
    public required string NewFolder { get; init; }

    /// <summary>Glob-style search pattern relative to each folder.</summary>
    public string SearchPattern { get; init; } = "*.pdf";

    public bool Recursive { get; init; } = true;

    public ComparisonOptions Options { get; init; } = new();

    /// <summary>Maximum number of file pairs compared concurrently. 0 = processor count.</summary>
    public int MaxDegreeOfParallelism { get; init; } = 0;
}

public enum FilePairStatus
{
    Identical,
    Differs,
    OnlyInOld,
    OnlyInNew,
    Error,
}

/// <summary>Result for a single matched (or unmatched) file pair within a batch.</summary>
public sealed record FilePairResult
{
    /// <summary>Path relative to the batch root, used to pair old and new files.</summary>
    public required string RelativePath { get; init; }

    public FilePairStatus Status { get; init; }

    public double Similarity { get; init; } = 1.0;

    public int DifferingPages { get; init; }

    /// <summary>Number of error messages detected inside the document content (e.g. "subreport error").</summary>
    public int ContentErrorCount { get; init; }

    public string? HighlightedPdfPath { get; init; }

    public string? Error { get; init; }
}

/// <summary>Aggregate report for a whole batch run.</summary>
public sealed record BatchComparisonReport
{
    public required string OldFolder { get; init; }
    public required string NewFolder { get; init; }

    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset CompletedAt { get; init; }

    public IReadOnlyList<FilePairResult> Files { get; init; } = [];

    public int Total => Files.Count;
    public int Identical => Files.Count(f => f.Status == FilePairStatus.Identical);
    public int Differing => Files.Count(f => f.Status == FilePairStatus.Differs);
    public int OnlyInOld => Files.Count(f => f.Status == FilePairStatus.OnlyInOld);
    public int OnlyInNew => Files.Count(f => f.Status == FilePairStatus.OnlyInNew);
    public int Errors => Files.Count(f => f.Status == FilePairStatus.Error);

    /// <summary>Files containing at least one detected content error.</summary>
    public int FilesWithContentErrors => Files.Count(f => f.ContentErrorCount > 0);
}
