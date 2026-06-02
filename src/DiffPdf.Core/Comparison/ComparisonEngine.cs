using DiffPdf.Core.Abstractions;
using DiffPdf.Core.Models;
using Microsoft.Extensions.Logging;

namespace DiffPdf.Core.Comparison;

/// <summary>
/// Orchestrates a single old/new PDF comparison: runs the requested modes,
/// merges per-page results, and optionally emits a highlighted diff PDF.
/// </summary>
public sealed class ComparisonEngine(
    IPdfTextExtractor textExtractor,
    ITextComparer textComparer,
    IVisualComparer visualComparer,
    IPdfPageRendererFactory rendererFactory,
    IHighlightedPdfWriter highlightedPdfWriter,
    ILogger<ComparisonEngine> logger) : IComparisonEngine
{
    public async Task<FileComparisonResult> CompareAsync(
        string oldPath,
        string newPath,
        ComparisonOptions options,
        string? artifactDirectory = null,
        CancellationToken ct = default)
    {
        int oldCount = textExtractor.GetPageCount(oldPath);
        int newCount = textExtractor.GetPageCount(newPath);

        var pageComparisons = new Dictionary<int, PageComparison>();

        if (options.Mode.HasFlag(ComparisonMode.Text))
        {
            var oldText = await textExtractor.ExtractAsync(oldPath, options.Pages, ct);
            var newText = await textExtractor.ExtractAsync(newPath, options.Pages, ct);
            Merge(pageComparisons, textComparer.Compare(oldText, newText, options));
        }

        if (options.Mode.HasFlag(ComparisonMode.Visual))
        {
            var visual = await visualComparer.CompareAsync(oldPath, newPath, options, ct);
            Merge(pageComparisons, visual);
        }

        var pages = pageComparisons.Values.OrderBy(p => p.PageNumber).ToList();

        string? highlightedPath = null;
        bool differs = pages.Any(p => p.IsDifferent) || oldCount != newCount;
        if (options.ProduceHighlightedPdf && differs && artifactDirectory is not null)
        {
            try
            {
                highlightedPath = await WriteHighlightedAsync(newPath, pages, options, artifactDirectory, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to write highlighted PDF for {New}", newPath);
            }
        }

        return new FileComparisonResult
        {
            OldPath = oldPath,
            NewPath = newPath,
            OldPageCount = oldCount,
            NewPageCount = newCount,
            Pages = pages,
            HighlightedPdfPath = highlightedPath,
        };
    }

    private static void Merge(Dictionary<int, PageComparison> acc, IReadOnlyList<PageComparison> incoming)
    {
        foreach (var pc in incoming)
        {
            if (acc.TryGetValue(pc.PageNumber, out var existing))
            {
                acc[pc.PageNumber] = existing with
                {
                    DifferenceScore = Math.Max(existing.DifferenceScore, pc.DifferenceScore),
                    Regions = [.. existing.Regions, .. pc.Regions],
                };
            }
            else
            {
                acc[pc.PageNumber] = pc;
            }
        }
    }

    private async Task<string> WriteHighlightedAsync(
        string newPath,
        IReadOnlyList<PageComparison> pages,
        ComparisonOptions options,
        string artifactDirectory,
        CancellationToken ct)
    {
        var renderer = rendererFactory.GetRenderer(options.Renderer);
        var highlighted = new List<HighlightedPage>();

        foreach (var page in pages.Where(p => p.IsDifferent))
        {
            var background = await renderer.RenderAsync(newPath, page.PageNumber, options.Dpi, ct);
            highlighted.Add(new HighlightedPage(background, page.Regions));
        }

        Directory.CreateDirectory(artifactDirectory);
        string outPath = Path.Combine(
            artifactDirectory,
            Path.GetFileNameWithoutExtension(newPath) + ".diff.pdf");

        await highlightedPdfWriter.WriteAsync(outPath, highlighted, ct);
        return outPath;
    }
}
