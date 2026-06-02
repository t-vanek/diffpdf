using DiffPdf.Core.Abstractions;
using DiffPdf.Core.Models;

namespace DiffPdf.Core.Comparison;

/// <summary>
/// Renders each page of both documents and diffs the bitmaps. Pixel-space
/// difference regions are converted to PDF points so they line up with the
/// textual regions.
/// </summary>
public sealed class VisualComparer(IPdfPageRendererFactory rendererFactory, IImageComparer imageComparer)
    : IVisualComparer
{
    public async Task<IReadOnlyList<PageComparison>> CompareAsync(
        string oldPath,
        string newPath,
        ComparisonOptions options,
        CancellationToken ct = default)
    {
        var renderer = rendererFactory.GetRenderer(options.Renderer);
        int oldCount = renderer.GetPageCount(oldPath);
        int newCount = renderer.GetPageCount(newPath);
        int maxPages = Math.Max(oldCount, newCount);

        var results = new List<PageComparison>();
        for (int page = 1; page <= maxPages; page++)
        {
            ct.ThrowIfCancellationRequested();
            if (!options.Pages.Contains(page)) continue;

            bool inOld = page <= oldCount, inNew = page <= newCount;

            // A page present in only one document is wholly added/removed.
            if (inOld ^ inNew)
            {
                results.Add(new PageComparison
                {
                    PageNumber = page,
                    DifferenceScore = 1.0,
                    Regions = [],
                });
                continue;
            }

            var oldPage = await renderer.RenderAsync(oldPath, page, options.Dpi, ct);
            var newPage = await renderer.RenderAsync(newPath, page, options.Dpi, ct);

            var diff = imageComparer.Compare(oldPage.Png, newPage.Png, options);

            double pageHeightPt = newPage.HeightPx * 72.0 / options.Dpi;
            var regions = diff.Regions
                .Select(px => new DifferenceRegion(
                    DifferenceKind.Changed,
                    CoordinateConverter.PixelToPoints(px, options.Dpi, newPage.HeightPx)))
                .ToList();

            results.Add(new PageComparison
            {
                PageNumber = page,
                DifferenceScore = diff.DifferenceRatio < options.VisualThreshold ? 0 : diff.DifferenceRatio,
                Regions = regions,
            });
        }

        return results;
    }
}
