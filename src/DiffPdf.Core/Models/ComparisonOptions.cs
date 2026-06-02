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
    /// <summary>Run both the textual and visual comparisons.</summary>
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

    // ---- Pixel-level visual diff ----

    /// <summary>
    /// Per-pixel channel tolerance (0-255) before a pixel counts as different.
    /// Set to 0 for an exact pixel-perfect comparison; higher values absorb
    /// anti-aliasing noise.
    /// </summary>
    public byte PixelTolerance { get; init; } = 16;

    /// <summary>
    /// Fraction of differing pixels on a page (0-1) below which the page is still
    /// considered visually identical. Set to 0 to flag a single differing pixel.
    /// </summary>
    public double VisualThreshold { get; init; } = 0.0005;

    /// <summary>
    /// Grid cell size (px) used to cluster differing pixels into highlight regions.
    /// Lower = finer regions (down to 1 = per-pixel); higher = coarser/faster.
    /// </summary>
    public int VisualClusterCellSize { get; init; } = 24;

    // ---- Text diff ----

    /// <summary>Whether to normalize whitespace before the textual diff.</summary>
    public bool NormalizeWhitespace { get; init; } = true;

    // ---- Page alignment ----

    /// <summary>
    /// Align pages by content so an inserted/removed page does not cascade into
    /// false differences. When false, pages are paired strictly by index.
    /// </summary>
    public bool AlignPages { get; init; } = true;

    /// <summary>
    /// Minimum word-overlap similarity (0-1) for two pages to align as "the same
    /// page that changed" rather than a separate add+remove. Kept low so a
    /// heavily edited page at the same position is still reported as TextChanged;
    /// only near-unrelated pages split into PageAdded/PageRemoved.
    /// </summary>
    public double PageMatchThreshold { get; init; } = 0.2;

    // ---- Blank page detection ----

    public bool DetectBlankPages { get; init; } = true;

    /// <summary>
    /// Max fraction of non-white (inked) pixels for a page to count as visually
    /// blank. A sparse text page typically has ~0.1-0.5% ink, so this is set well
    /// below that; a truly blank page renders at ~0.
    /// </summary>
    public double BlankPageThreshold { get; init; } = 0.0002;

    // ---- Content error detection ----

    /// <summary>Scan extracted text for known error messages (e.g. "subreport error").</summary>
    public bool DetectContentErrors { get; init; } = true;

    /// <summary>
    /// Case-insensitive regex patterns flagged as content errors. Defaults cover
    /// common report-generation failures rendered into the PDF.
    /// </summary>
    public IReadOnlyList<string> ContentErrorPatterns { get; init; } = DefaultContentErrorPatterns;

    public static readonly IReadOnlyList<string> DefaultContentErrorPatterns =
    [
        @"subreport\s+error",
        @"#error",
        @"#ref!",
        @"error\s+rendering",
        @"evaluation\s+warning",
        @"could\s+not\s+be\s+rendered",
    ];

    // ---- Output ----

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
