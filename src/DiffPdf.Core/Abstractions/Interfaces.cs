using DiffPdf.Core.Models;

namespace DiffPdf.Core.Abstractions;

/// <summary>Extracts positioned text from a PDF and probes its readability.</summary>
public interface IPdfTextExtractor
{
    /// <summary>Opens the document defensively and reports its status + geometry.</summary>
    DocumentInfo Probe(string path);

    Task<IReadOnlyList<PageText>> ExtractAsync(string path, PageRange range, CancellationToken ct = default);
}

/// <summary>Renders PDF pages to raster images.</summary>
public interface IPdfPageRenderer
{
    RendererBackend Backend { get; }
    int GetPageCount(string path);
    Task<RenderedPage> RenderAsync(string path, int pageNumber, int dpi, CancellationToken ct = default);
}

/// <summary>Resolves a renderer for a requested backend.</summary>
public interface IPdfPageRendererFactory
{
    IPdfPageRenderer GetRenderer(RendererBackend backend);
}

/// <summary>Compares two page bitmaps at the pixel level.</summary>
public interface IImageComparer
{
    /// <param name="ignoreRegionsPx">Pixel-space (top-left) rectangles to exclude from the diff.</param>
    ImageDiffResult Compare(
        byte[] oldPng,
        byte[] newPng,
        ComparisonOptions options,
        IReadOnlyList<RectangleD>? ignoreRegionsPx = null);
}

/// <summary>Decides whether a rendered page is (visually) blank.</summary>
public interface IBlankPageDetector
{
    bool IsVisuallyBlank(RenderedPage page, ComparisonOptions options);
}

/// <summary>Per-page textual diff result.</summary>
public sealed record TextPageDiff(double Score, IReadOnlyList<DifferenceRegion> Regions)
{
    public static readonly TextPageDiff Identical = new(0, []);
}

/// <summary>Word-level textual diff over a single aligned page pair.</summary>
public interface ITextComparer
{
    TextPageDiff ComparePage(PageText? oldPage, PageText? newPage, ComparisonOptions options);

    /// <summary>Text similarity (0-1) used for page alignment.</summary>
    double Similarity(PageText oldPage, PageText newPage);
}

/// <summary>One column of a page alignment; a null side means insertion/deletion.</summary>
public sealed record AlignedPagePair(int? OldPageNumber, int? NewPageNumber);

/// <summary>Aligns the pages of two documents to absorb inserts/deletes.</summary>
public interface IPageAligner
{
    IReadOnlyList<AlignedPagePair> Align(
        IReadOnlyList<PageText> oldPages,
        IReadOnlyList<PageText> newPages,
        ComparisonOptions options);
}

/// <summary>Scans extracted text for known error messages rendered into the PDF.</summary>
public interface IContentErrorDetector
{
    IReadOnlyList<ContentError> Detect(
        IReadOnlyList<PageText> pages,
        ContentErrorSide side,
        ComparisonOptions options);
}

/// <summary>One side (old or new) of a diff spread: a rendered page plus its regions.</summary>
public sealed record HighlightSide(RenderedPage Render, IReadOnlyList<DifferenceRegion> Regions);

/// <summary>
/// A single differing page presented as an old/new pair. Either side may be
/// null for an added (no old) or removed (no new) page.
/// </summary>
public sealed record DiffSpread(int? OldPageNumber, int? NewPageNumber, HighlightSide? Old, HighlightSide? New);

/// <summary>Assembles a highlighted diff PDF.</summary>
public interface IHighlightedPdfWriter
{
    Task WriteAsync(
        string outputPath,
        IReadOnlyList<DiffSpread> spreads,
        HighlightLayout layout,
        CancellationToken ct = default);
}

/// <summary>Top-level orchestrator: compares a single old/new PDF pair.</summary>
public interface IComparisonEngine
{
    Task<FileComparisonResult> CompareAsync(
        string oldPath,
        string newPath,
        ComparisonOptions options,
        string? artifactDirectory = null,
        CancellationToken ct = default);
}

/// <summary>Compares whole folders of PDFs (old vs new).</summary>
public interface IBatchComparer
{
    Task<BatchComparisonReport> CompareAsync(
        BatchComparisonRequest request,
        string artifactDirectory,
        IProgress<int>? progress = null,
        CancellationToken ct = default);
}
