namespace DiffPdf.Core.Models;

public enum DifferenceKind
{
    /// <summary>Content present in the new document but not the old.</summary>
    Added,
    /// <summary>Content present in the old document but not the new.</summary>
    Removed,
    /// <summary>Content changed between old and new.</summary>
    Changed,
}

/// <summary>A single highlighted region on a page.</summary>
public sealed record DifferenceRegion(
    DifferenceKind Kind,
    RectangleD BoundingBox,
    string? OldText = null,
    string? NewText = null);

/// <summary>Outcome of comparing one page of the old document against the new.</summary>
public sealed record PageComparison
{
    public required int PageNumber { get; init; }

    /// <summary>0 = identical, 1 = completely different.</summary>
    public double DifferenceScore { get; init; }

    public bool IsDifferent => Regions.Count > 0 || DifferenceScore > 0;

    public IReadOnlyList<DifferenceRegion> Regions { get; init; } = [];
}

/// <summary>Full result of comparing a single old/new PDF pair.</summary>
public sealed record FileComparisonResult
{
    public required string OldPath { get; init; }
    public required string NewPath { get; init; }

    public int OldPageCount { get; init; }
    public int NewPageCount { get; init; }

    public IReadOnlyList<PageComparison> Pages { get; init; } = [];

    /// <summary>Relative path to the generated highlighted diff PDF, if any.</summary>
    public string? HighlightedPdfPath { get; init; }

    public bool AreIdentical => Pages.All(p => !p.IsDifferent) && OldPageCount == NewPageCount;

    /// <summary>Aggregate similarity across pages (0-1).</summary>
    public double Similarity =>
        Pages.Count == 0 ? 1.0 : 1.0 - Pages.Average(p => p.DifferenceScore);
}
