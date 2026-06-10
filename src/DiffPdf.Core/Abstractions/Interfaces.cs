using DiffPdf.Core.Models;

namespace DiffPdf.Core.Abstractions;

/// <summary>Extracts positioned text from a PDF and probes its readability.</summary>
public interface IPdfTextExtractor
{
    /// <summary>Opens the document defensively and reports its status + geometry.</summary>
    DocumentInfo Probe(string path);

    Task<IReadOnlyList<PageText>> ExtractAsync(string path, PageRange range, CancellationToken ct = default);

    /// <summary>
    /// Probe + extraction from a single document open (and a single page pass): returns the document
    /// status/geometry together with the positioned text; extraction is skipped when the document is not
    /// comparable. The default routes through <see cref="Probe"/> + <see cref="ExtractAsync"/> (two opens)
    /// so existing implementations keep working — real extractors override it with a one-open implementation.
    /// </summary>
    async Task<DocumentExtraction> ProbeAndExtractAsync(string path, PageRange range, CancellationToken ct = default)
    {
        var info = Probe(path);
        IReadOnlyList<PageText> pages = info.IsComparable ? await ExtractAsync(path, range, ct) : [];
        return new DocumentExtraction(info, pages);
    }
}

/// <summary>Result of <see cref="IPdfTextExtractor.ProbeAndExtractAsync"/>: document status + extracted pages
/// (empty when the document is not comparable).</summary>
public sealed record DocumentExtraction(DocumentInfo Info, IReadOnlyList<PageText> Pages);

/// <summary>Renders PDF pages to raster images.</summary>
public interface IPdfPageRenderer
{
    RendererBackend Backend { get; }
    int GetPageCount(string path);
    Task<RenderedPage> RenderAsync(string path, int pageNumber, int dpi, CancellationToken ct = default);

    /// <summary>
    /// Probes whether this rendering backend is actually usable (operational readiness). The default
    /// reports available with no version — correct for in-process backends with no external binary
    /// (e.g. PDFium); backends that shell out (Ghostscript) override this to check the executable.
    /// </summary>
    Task<RendererHealth> CheckAsync(CancellationToken ct = default) =>
        Task.FromResult(new RendererHealth(Backend, Available: true, Version: null));
}

/// <summary>Result of an <see cref="IPdfPageRenderer"/> availability probe.</summary>
public sealed record RendererHealth(RendererBackend Backend, bool Available, string? Version);

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

/// <summary>
/// One side (old or new) of a diff spread: the source PDF + page number and the
/// difference regions (in PDF points). <see cref="Render"/> is only populated for
/// the raster writer; the vector writer reads the original page directly.
/// </summary>
public sealed record HighlightSide(
    string SourcePdfPath,
    int PageNumber,
    IReadOnlyList<DifferenceRegion> Regions,
    RenderedPage? Render = null);

/// <summary>
/// A single differing page presented as an old/new pair. Either side may be
/// null for an added (no old) or removed (no new) page.
/// </summary>
public sealed record DiffSpread(int? OldPageNumber, int? NewPageNumber, HighlightSide? Old, HighlightSide? New);

/// <summary>Assembles a highlighted diff PDF.</summary>
public interface IHighlightedPdfWriter
{
    HighlightStyle Style { get; }

    Task WriteAsync(
        string outputPath,
        IReadOnlyList<DiffSpread> spreads,
        HighlightLayout layout,
        CancellationToken ct = default);
}

/// <summary>Selects a highlighted-PDF writer by style.</summary>
public interface IHighlightedPdfWriterFactory
{
    IHighlightedPdfWriter Get(HighlightStyle style);
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

/// <summary>An established (or pass-through) access to a folder. Dispose releases it.</summary>
public sealed class NetworkShareConnection(string path, Action? onDispose = null) : IDisposable
{
    /// <summary>Local path to enumerate (the input path, or a temporary mount point).</summary>
    public string Path { get; } = path;

    public void Dispose() => onDispose?.Invoke();
}

/// <summary>
/// Establishes access to a (possibly authenticated, possibly network) folder and
/// returns the path to read from. A pass-through for local / already-mounted paths.
/// </summary>
public interface INetworkShareConnector
{
    NetworkShareConnection Connect(string folder, NetworkCredentials? credentials);
}
