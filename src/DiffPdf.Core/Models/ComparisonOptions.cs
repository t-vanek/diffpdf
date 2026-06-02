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

    /// <summary>
    /// Overall engine strictness. Drives the default difference-reporting
    /// tolerances (pixel tolerance, visual + text thresholds). Individual knobs
    /// below override the preset when set explicitly.
    /// </summary>
    public Strictness Strictness { get; init; } = Strictness.Balanced;

    // ---- Pixel-level visual diff (null => derived from Strictness) ----

    /// <summary>
    /// Per-pixel channel tolerance (0-255) before a pixel counts as different.
    /// 0 = exact pixel-perfect; higher values absorb anti-aliasing noise.
    /// </summary>
    public byte? PixelTolerance { get; init; }

    /// <summary>
    /// Fraction of differing pixels on a page (0-1) below which the page is still
    /// considered visually identical. 0 flags a single differing pixel.
    /// </summary>
    public double? VisualThreshold { get; init; }

    /// <summary>
    /// Fraction of changed words on a page (0-1) below which a text change is
    /// ignored. 0 flags any text change.
    /// </summary>
    public double? TextDifferenceThreshold { get; init; }

    /// <summary>
    /// Radius (px) within which a differing pixel that has a matching pixel in
    /// the other image is treated as merely shifted (anti-aliasing / sub-pixel
    /// movement) rather than a real change. 0 = strict positional comparison.
    /// </summary>
    public int? ShiftTolerance { get; init; }

    // ---- Effective tolerances (preset + overrides) ----

    public byte EffectivePixelTolerance => PixelTolerance ?? Strictness switch
    {
        Strictness.Exact => 0,
        Strictness.Strict => 4,
        Strictness.Lenient => 40,
        _ => 16,
    };

    public double EffectiveVisualThreshold => VisualThreshold ?? Strictness switch
    {
        Strictness.Exact => 0.0,
        Strictness.Strict => 0.0001,
        Strictness.Lenient => 0.005,
        _ => 0.0005,
    };

    public double EffectiveTextDifferenceThreshold => TextDifferenceThreshold ?? Strictness switch
    {
        Strictness.Lenient => 0.02,
        _ => 0.0,
    };

    public int EffectiveShiftTolerance => ShiftTolerance ?? Strictness switch
    {
        Strictness.Lenient => 2,
        Strictness.Balanced => 1,
        _ => 0,
    };

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

    // ---- Ignored / dynamic content ----

    /// <summary>
    /// Rectangular areas excluded from both the text and visual comparison —
    /// e.g. a footer carrying a generation date/time, a page number, or a
    /// watermark that legitimately changes between runs.
    /// </summary>
    public IReadOnlyList<IgnoreRegion> IgnoreRegions { get; init; } = [];

    /// <summary>
    /// Case-insensitive regex patterns; any word matching one is dropped before
    /// the text diff. Useful for timestamps/dates that move around the page.
    /// </summary>
    public IReadOnlyList<string> IgnoreTextPatterns { get; init; } = [];

    // ---- Output ----

    /// <summary>Produce a highlighted diff PDF artifact for differing files.</summary>
    public bool ProduceHighlightedPdf { get; init; } = true;

    /// <summary>
    /// Layout of the highlighted diff PDF. <see cref="HighlightLayout.SideBySide"/>
    /// shows old (left) and new (right) on one spread; <see cref="HighlightLayout.Single"/>
    /// shows only the changed side.
    /// </summary>
    public HighlightLayout HighlightLayout { get; init; } = HighlightLayout.SideBySide;

    /// <summary>
    /// How the highlighted diff PDF is rendered. <see cref="HighlightStyle.Raster"/>
    /// rasterizes pages (works for any backend, image output). <see cref="HighlightStyle.VectorOverlay"/>
    /// overlays highlights on the original PDF pages, keeping the original vector
    /// content and selectable text.
    /// </summary>
    public HighlightStyle HighlightStyle { get; init; } = HighlightStyle.Raster;

    /// <summary>Preferred renderer backend for the visual mode.</summary>
    public RendererBackend Renderer { get; init; } = RendererBackend.Ghostscript;
}

public enum RendererBackend
{
    Ghostscript,
    Pdfium,
}

public enum HighlightLayout
{
    /// <summary>Old (left) and new (right) side by side on one spread.</summary>
    SideBySide,
    /// <summary>Only the changed side (new for added/changed, old for removed).</summary>
    Single,
}

public enum HighlightStyle
{
    /// <summary>Rasterize each page to an image and draw highlights on top.</summary>
    Raster,
    /// <summary>Overlay highlights on the original PDF pages — text stays selectable.</summary>
    VectorOverlay,
}

/// <summary>Engine strictness presets, from pixel-perfect to forgiving.</summary>
public enum Strictness
{
    /// <summary>Pixel-perfect: any differing pixel or word is reported.</summary>
    Exact,
    /// <summary>Tight tolerances; absorbs only minimal rendering noise.</summary>
    Strict,
    /// <summary>Sensible defaults for most report comparisons.</summary>
    Balanced,
    /// <summary>Forgiving; ignores small visual noise and tiny text changes.</summary>
    Lenient,
}
