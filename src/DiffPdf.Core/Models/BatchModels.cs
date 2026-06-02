namespace DiffPdf.Core.Models;

/// <summary>Request to compare every PDF under <see cref="OldFolder"/> against <see cref="NewFolder"/>.</summary>
public sealed record BatchComparisonRequest
{
    /// <summary>Business instance + project this job belongs to.</summary>
    public required JobScope Scope { get; init; }

    public required string OldFolder { get; init; }
    public required string NewFolder { get; init; }

    /// <summary>Glob-style search pattern relative to each folder.</summary>
    public string SearchPattern { get; init; } = "*.pdf";

    public bool Recursive { get; init; } = true;

    public ComparisonOptions Options { get; init; } = new();

    /// <summary>Maximum number of file pairs compared concurrently. 0 = processor count.</summary>
    public int MaxDegreeOfParallelism { get; init; } = 0;

    /// <summary>Optional pass/fail criteria for CI gating. Null = no gating.</summary>
    public BatchGate? Gate { get; init; }

    /// <summary>Inline credentials for the old folder if it is an authenticated network share.</summary>
    public NetworkCredentials? OldFolderCredentials { get; init; }

    /// <summary>Inline credentials for the new folder if it is an authenticated network share.</summary>
    public NetworkCredentials? NewFolderCredentials { get; init; }

    /// <summary>
    /// Name of a configured credential profile for the old folder. Preferred over
    /// <see cref="OldFolderCredentials"/> so passwords stay in configuration.
    /// </summary>
    public string? OldFolderCredentialProfile { get; init; }

    /// <summary>Name of a configured credential profile for the new folder.</summary>
    public string? NewFolderCredentialProfile { get; init; }
}

/// <summary>
/// Pass/fail thresholds for a batch run, for use as a CI gate. A null limit
/// means "no limit". <see cref="FailOnAnyDifference"/> forces zero tolerance for
/// differing files.
/// </summary>
public sealed record BatchGate
{
    public bool FailOnAnyDifference { get; init; }
    public int? MaxDifferingFiles { get; init; }
    public int? MaxErrors { get; init; }
    public int? MaxFilesWithContentErrors { get; init; }

    public IReadOnlyList<string> Evaluate(int differing, int errors, int filesWithContentErrors)
    {
        var violations = new List<string>();

        int maxDiff = FailOnAnyDifference ? 0 : MaxDifferingFiles ?? int.MaxValue;
        if (differing > maxDiff)
            violations.Add($"differing files {differing} exceeds limit {maxDiff}");

        if (errors > (MaxErrors ?? int.MaxValue))
            violations.Add($"errored files {errors} exceeds limit {MaxErrors}");

        if (filesWithContentErrors > (MaxFilesWithContentErrors ?? int.MaxValue))
            violations.Add($"files with content errors {filesWithContentErrors} exceeds limit {MaxFilesWithContentErrors}");

        return violations;
    }
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

    /// <summary>Gate evaluated against this report; null = no gating.</summary>
    public BatchGate? Gate { get; init; }

    public int Total => Files.Count;
    public int Identical => Files.Count(f => f.Status == FilePairStatus.Identical);
    public int Differing => Files.Count(f => f.Status == FilePairStatus.Differs);
    public int OnlyInOld => Files.Count(f => f.Status == FilePairStatus.OnlyInOld);
    public int OnlyInNew => Files.Count(f => f.Status == FilePairStatus.OnlyInNew);
    public int Errors => Files.Count(f => f.Status == FilePairStatus.Error);

    /// <summary>Files containing at least one detected content error.</summary>
    public int FilesWithContentErrors => Files.Count(f => f.ContentErrorCount > 0);

    /// <summary>Gate violations for this run; empty when there is no gate or it passed.</summary>
    public IReadOnlyList<string> GateViolations =>
        Gate?.Evaluate(Differing, Errors, FilesWithContentErrors) ?? [];

    /// <summary>True when no gate is configured or all its criteria are satisfied.</summary>
    public bool Passed => GateViolations.Count == 0;
}
