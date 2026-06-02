namespace DiffPdf.Core.Models;

/// <summary>Readability state of a single source PDF.</summary>
public enum DocumentStatus
{
    Ok,
    /// <summary>File missing on disk.</summary>
    NotFound,
    /// <summary>File exists but could not be parsed (corrupt / not a PDF).</summary>
    Unreadable,
    /// <summary>Encrypted / password-protected and could not be opened.</summary>
    Encrypted,
    /// <summary>Opened fine but contains zero pages.</summary>
    Empty,
}

/// <summary>Per-page geometry, used for size/orientation comparison.</summary>
public sealed record PageGeometry(int PageNumber, double Width, double Height, int Rotation)
{
    /// <summary>Width/height after applying the page rotation (0/180 vs 90/270).</summary>
    public (double W, double H) Effective =>
        (Rotation % 180 == 0) ? (Width, Height) : (Height, Width);
}

/// <summary>Result of probing a PDF before comparison.</summary>
public sealed record DocumentInfo
{
    public required DocumentStatus Status { get; init; }
    public string? Message { get; init; }
    public int PageCount { get; init; }
    public IReadOnlyList<PageGeometry> Pages { get; init; } = [];

    /// <summary>True when the document can participate in a comparison (even if empty).</summary>
    public bool IsComparable => Status is DocumentStatus.Ok or DocumentStatus.Empty;

    public static DocumentInfo Failed(DocumentStatus status, string message) =>
        new() { Status = status, Message = message };
}

/// <summary>Top-level outcome of comparing a pair.</summary>
public enum ComparisonOutcome
{
    /// <summary>Both documents were readable and a comparison was produced.</summary>
    Compared,
    /// <summary>At least one document could not be read; no comparison possible.</summary>
    Failed,
}
