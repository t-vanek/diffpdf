namespace DiffPdf.Core.Models;

/// <summary>Which comparison strategy (or strategies) to run.</summary>
[Flags]
public enum ComparisonMode
{
    None = 0,
    /// <summary>Extract words and run a textual diff.</summary>
    Text = 1,
    /// <summary>Render pages to bitmaps and run a visual/pixel diff.</summary>
    Visual = 2,
    /// <summary>Run a cheap pre-filter (page count, byte hash) before the expensive modes.</summary>
    Both = Text | Visual,
}

/// <summary>1-based inclusive page range. <c>null</c> bounds mean "open ended".</summary>
public readonly record struct PageRange(int? From = null, int? To = null)
{
    public static readonly PageRange All = new(null, null);

    public bool Contains(int pageNumber) =>
        (From is null || pageNumber >= From) && (To is null || pageNumber <= To);
}

/// <summary>Tunable knobs for a single comparison run.</summary>
public sealed record ComparisonOptions
{
    public ComparisonMode Mode { get; init; } = ComparisonMode.Both;

    public PageRange Pages { get; init; } = PageRange.All;

    /// <summary>Render resolution for the visual comparison.</summary>
    public int Dpi { get; init; } = 150;

    /// <summary>
    /// Per-pixel channel tolerance (0-255) before a pixel counts as different.
    /// Absorbs anti-aliasing noise.
    /// </summary>
    public byte PixelTolerance { get; init; } = 16;

    /// <summary>
    /// Fraction of differing pixels on a page (0-1) below which the page is still
    /// considered visually identical.
    /// </summary>
    public double VisualThreshold { get; init; } = 0.0005;

    /// <summary>Whether to normalize whitespace before the textual diff.</summary>
    public bool NormalizeWhitespace { get; init; } = true;

    /// <summary>Produce a highlighted diff PDF artifact for differing files.</summary>
    public bool ProduceHighlightedPdf { get; init; } = true;

    /// <summary>Preferred renderer backend for the visual mode.</summary>
    public RendererBackend Renderer { get; init; } = RendererBackend.Ghostscript;
}

public enum RendererBackend
{
    Ghostscript,
    Pdfium,
}
