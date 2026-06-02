using DiffPdf.Core.Models;

namespace DiffPdf.Core.Abstractions;

/// <summary>A word together with its bounding box on the page.</summary>
public sealed record PositionedWord(string Text, RectangleD BoundingBox);

/// <summary>Extracted text content of a single page.</summary>
public sealed record PageText
{
    public required int PageNumber { get; init; }
    public double Width { get; init; }
    public double Height { get; init; }
    public IReadOnlyList<PositionedWord> Words { get; init; } = [];
}

/// <summary>A page rendered to a raster image (PNG bytes).</summary>
public sealed record RenderedPage
{
    public required int PageNumber { get; init; }
    public int WidthPx { get; init; }
    public int HeightPx { get; init; }
    public int Dpi { get; init; }
    public required byte[] Png { get; init; }
}

/// <summary>Result of comparing two page bitmaps.</summary>
public sealed record ImageDiffResult
{
    /// <summary>Fraction of pixels that differ beyond tolerance (0-1).</summary>
    public double DifferenceRatio { get; init; }

    /// <summary>Bounding boxes (in pixels, top-left origin) of clustered differences.</summary>
    public IReadOnlyList<RectangleD> Regions { get; init; } = [];

    /// <summary>Optional visualization with differences highlighted (PNG bytes).</summary>
    public byte[]? DiffImagePng { get; init; }
}
