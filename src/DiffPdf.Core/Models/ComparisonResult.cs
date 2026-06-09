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

/// <summary>A single highlighted region on a page (coordinates in PDF points, bottom-left origin).</summary>
public sealed record DifferenceRegion(
    DifferenceKind Kind,
    RectangleD BoundingBox,
    string? OldText = null,
    string? NewText = null);

/// <summary>Typed classification of how a page changed. Combinable.</summary>
[Flags]
public enum PageChangeType
{
    None = 0,
    TextChanged = 1 << 0,
    VisualChanged = 1 << 1,
    PageAdded = 1 << 2,
    PageRemoved = 1 << 3,
    /// <summary>Page dimensions or orientation differ.</summary>
    SizeChanged = 1 << 4,
    /// <summary>Page had content in old but is blank in new.</summary>
    BecameBlank = 1 << 5,
    /// <summary>Page was blank in old but has content in new.</summary>
    WasBlank = 1 << 6,
    /// <summary>The page could not be rendered/extracted/compared (a per-page failure was contained, not fatal).</summary>
    ProcessingError = 1 << 7,
}

/// <summary>Outcome of comparing one aligned page pair (either side may be absent).</summary>
public sealed record PageComparison
{
    /// <summary>1-based page number in the old document, or null if the page was added.</summary>
    public int? OldPageNumber { get; init; }
    /// <summary>1-based page number in the new document, or null if the page was removed.</summary>
    public int? NewPageNumber { get; init; }

    public PageChangeType Changes { get; init; } = PageChangeType.None;

    /// <summary>0 = identical, 1 = completely different.</summary>
    public double DifferenceScore { get; init; }

    public bool OldBlank { get; init; }
    public bool NewBlank { get; init; }

    public IReadOnlyList<DifferenceRegion> Regions { get; init; } = [];

    public bool IsDifferent => Changes != PageChangeType.None;

    /// <summary>Best page number to display this comparison under.</summary>
    public int DisplayPageNumber => NewPageNumber ?? OldPageNumber ?? 0;
}

public enum ContentErrorSide { Old, New, Engine }

/// <summary>
/// Something wrong on a page: either a pattern detected inside the document text (e.g. "subreport error",
/// <see cref="ContentErrorSide.Old"/>/<see cref="ContentErrorSide.New"/>), or a per-page processing failure the
/// engine contained (<see cref="ContentErrorSide.Engine"/>; <c>Pattern</c> = "render"/"extract"/"processing").
/// </summary>
public sealed record ContentError(
    ContentErrorSide Side,
    int PageNumber,
    string Pattern,
    string Snippet);

/// <summary>Full result of comparing a single old/new PDF pair.</summary>
public sealed record FileComparisonResult
{
    public required string OldPath { get; init; }
    public required string NewPath { get; init; }

    public ComparisonOutcome Outcome { get; init; } = ComparisonOutcome.Compared;
    public DocumentStatus OldStatus { get; init; } = DocumentStatus.Ok;
    public DocumentStatus NewStatus { get; init; } = DocumentStatus.Ok;
    public string? Error { get; init; }

    public int OldPageCount { get; init; }
    public int NewPageCount { get; init; }

    public IReadOnlyList<PageComparison> Pages { get; init; } = [];

    /// <summary>Error messages detected inside the documents' content.</summary>
    public IReadOnlyList<ContentError> ContentErrors { get; init; } = [];

    /// <summary>Relative path to the generated highlighted diff PDF, if any.</summary>
    public string? HighlightedPdfPath { get; init; }

    public int DifferingPages => Pages.Count(p => p.IsDifferent);

    public bool HasContentErrors => ContentErrors.Count > 0;

    /// <summary>True when the pair was compared successfully and no page differs.</summary>
    public bool AreIdentical =>
        Outcome == ComparisonOutcome.Compared && DifferingPages == 0;

    /// <summary>Aggregate similarity across pages (0-1).</summary>
    public double Similarity =>
        Outcome != ComparisonOutcome.Compared ? 0
        : Pages.Count == 0 ? 1.0
        : 1.0 - Pages.Average(p => p.DifferenceScore);
}
