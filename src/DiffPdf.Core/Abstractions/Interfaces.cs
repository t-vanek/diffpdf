using DiffPdf.Core.Models;

namespace DiffPdf.Core.Abstractions;

/// <summary>Extracts positioned text from a PDF.</summary>
public interface IPdfTextExtractor
{
    int GetPageCount(string path);
    Task<IReadOnlyList<PageText>> ExtractAsync(string path, PageRange range, CancellationToken ct = default);
}

/// <summary>Renders PDF pages to raster images.</summary>
public interface IPdfPageRenderer
{
    /// <summary>Backend this renderer implements.</summary>
    RendererBackend Backend { get; }

    int GetPageCount(string path);
    Task<RenderedPage> RenderAsync(string path, int pageNumber, int dpi, CancellationToken ct = default);
}

/// <summary>Resolves a renderer for a requested backend.</summary>
public interface IPdfPageRendererFactory
{
    IPdfPageRenderer GetRenderer(RendererBackend backend);
}

/// <summary>Compares two page bitmaps.</summary>
public interface IImageComparer
{
    ImageDiffResult Compare(byte[] oldPng, byte[] newPng, ComparisonOptions options);
}

/// <summary>Page-level input for the highlighted PDF writer.</summary>
public sealed record HighlightedPage(RenderedPage Background, IReadOnlyList<DifferenceRegion> Regions);

/// <summary>Assembles a highlighted diff PDF.</summary>
public interface IHighlightedPdfWriter
{
    Task WriteAsync(string outputPath, IReadOnlyList<HighlightedPage> pages, CancellationToken ct = default);
}

/// <summary>Textual diff over the words of two documents.</summary>
public interface ITextComparer
{
    IReadOnlyList<PageComparison> Compare(
        IReadOnlyList<PageText> oldPages,
        IReadOnlyList<PageText> newPages,
        ComparisonOptions options);
}

/// <summary>Visual diff over the rendered pages of two documents.</summary>
public interface IVisualComparer
{
    Task<IReadOnlyList<PageComparison>> CompareAsync(
        string oldPath,
        string newPath,
        ComparisonOptions options,
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
