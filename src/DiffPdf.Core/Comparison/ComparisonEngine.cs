using DiffPdf.Core.Abstractions;
using DiffPdf.Core.Models;
using Microsoft.Extensions.Logging;

namespace DiffPdf.Core.Comparison;

/// <summary>
/// Orchestrates a single old/new PDF comparison: probes readability, detects
/// content errors, aligns pages, runs the requested text/visual diffs, detects
/// blank pages and size changes, classifies each page, and optionally emits a
/// highlighted diff PDF.
/// </summary>
public sealed class ComparisonEngine(
    IPdfTextExtractor textExtractor,
    ITextComparer textComparer,
    IImageComparer imageComparer,
    IBlankPageDetector blankDetector,
    IPageAligner pageAligner,
    IContentErrorDetector contentErrorDetector,
    IPdfPageRendererFactory rendererFactory,
    IHighlightedPdfWriter highlightedPdfWriter,
    ILogger<ComparisonEngine> logger) : IComparisonEngine
{
    private const double SizeTolerancePoints = 1.0;

    public async Task<FileComparisonResult> CompareAsync(
        string oldPath,
        string newPath,
        ComparisonOptions options,
        string? artifactDirectory = null,
        CancellationToken ct = default)
    {
        var oldInfo = textExtractor.Probe(oldPath);
        var newInfo = textExtractor.Probe(newPath);

        // Hard read errors on either side: no comparison possible.
        if (!oldInfo.IsComparable || !newInfo.IsComparable)
        {
            return new FileComparisonResult
            {
                OldPath = oldPath,
                NewPath = newPath,
                Outcome = ComparisonOutcome.Failed,
                OldStatus = oldInfo.Status,
                NewStatus = newInfo.Status,
                OldPageCount = oldInfo.PageCount,
                NewPageCount = newInfo.PageCount,
                Error = FormatError(oldInfo, newInfo),
            };
        }

        var oldText = await textExtractor.ExtractAsync(oldPath, options.Pages, ct);
        var newText = await textExtractor.ExtractAsync(newPath, options.Pages, ct);

        var contentErrors = new List<ContentError>();
        if (options.DetectContentErrors)
        {
            contentErrors.AddRange(contentErrorDetector.Detect(oldText, ContentErrorSide.Old, options));
            contentErrors.AddRange(contentErrorDetector.Detect(newText, ContentErrorSide.New, options));
        }

        var oldByNum = oldText.ToDictionary(p => p.PageNumber);
        var newByNum = newText.ToDictionary(p => p.PageNumber);
        var oldGeom = oldInfo.Pages.ToDictionary(g => g.PageNumber);
        var newGeom = newInfo.Pages.ToDictionary(g => g.PageNumber);

        var pairs = pageAligner.Align(oldText, newText, options);

        var pageComparisons = new List<PageComparison>(pairs.Count);
        var spreads = new List<DiffSpread>();

        foreach (var pair in pairs)
        {
            ct.ThrowIfCancellationRequested();
            var (comparison, spread) = await ComparePairAsync(
                pair, oldPath, newPath, oldByNum, newByNum, oldGeom, newGeom, options, artifactDirectory is not null, ct);
            pageComparisons.Add(comparison);
            if (spread is not null) spreads.Add(spread);
        }

        string? highlightedPath = null;
        if (options.ProduceHighlightedPdf && artifactDirectory is not null && spreads.Count > 0)
        {
            try
            {
                highlightedPath = await WriteHighlightedAsync(newPath, spreads, options.HighlightLayout, artifactDirectory, ct);
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
            Outcome = ComparisonOutcome.Compared,
            OldStatus = oldInfo.Status,
            NewStatus = newInfo.Status,
            OldPageCount = oldInfo.PageCount,
            NewPageCount = newInfo.PageCount,
            Pages = pageComparisons,
            ContentErrors = contentErrors,
            HighlightedPdfPath = highlightedPath,
        };
    }

    private async Task<(PageComparison, DiffSpread?)> ComparePairAsync(
        AlignedPagePair pair,
        string oldPath,
        string newPath,
        IReadOnlyDictionary<int, PageText> oldByNum,
        IReadOnlyDictionary<int, PageText> newByNum,
        IReadOnlyDictionary<int, PageGeometry> oldGeom,
        IReadOnlyDictionary<int, PageGeometry> newGeom,
        ComparisonOptions options,
        bool wantHighlight,
        CancellationToken ct)
    {
        var renderer = rendererFactory.GetRenderer(options.Renderer);
        bool doText = options.Mode.HasFlag(ComparisonMode.Text);
        bool doVisual = options.Mode.HasFlag(ComparisonMode.Visual);
        bool needRenders = doVisual || options.DetectBlankPages || wantHighlight;

        // --- Page added (only in new) ---
        if (pair.OldPageNumber is null && pair.NewPageNumber is int addedNum)
        {
            RenderedPage? render = needRenders ? await renderer.RenderAsync(newPath, addedNum, options.Dpi, ct) : null;
            bool blank = render is not null && blankDetector.IsVisuallyBlank(render, options);
            var comparison = new PageComparison
            {
                NewPageNumber = addedNum,
                Changes = PageChangeType.PageAdded,
                DifferenceScore = 1.0,
                NewBlank = blank,
            };
            var spread = wantHighlight && render is not null
                ? new DiffSpread(null, addedNum, Old: null,
                    New: new HighlightSide(render, [FullPageRegion(render, DifferenceKind.Added)]))
                : null;
            return (comparison, spread);
        }

        // --- Page removed (only in old) ---
        if (pair.NewPageNumber is null && pair.OldPageNumber is int removedNum)
        {
            RenderedPage? render = needRenders ? await renderer.RenderAsync(oldPath, removedNum, options.Dpi, ct) : null;
            bool blank = render is not null && blankDetector.IsVisuallyBlank(render, options);
            var comparison = new PageComparison
            {
                OldPageNumber = removedNum,
                Changes = PageChangeType.PageRemoved,
                DifferenceScore = 1.0,
                OldBlank = blank,
            };
            var spread = wantHighlight && render is not null
                ? new DiffSpread(removedNum, null,
                    Old: new HighlightSide(render, [FullPageRegion(render, DifferenceKind.Removed)]), New: null)
                : null;
            return (comparison, spread);
        }

        // --- Matched pair ---
        int oldNum = pair.OldPageNumber!.Value;
        int newNum = pair.NewPageNumber!.Value;
        oldByNum.TryGetValue(oldNum, out var oldPage);
        newByNum.TryGetValue(newNum, out var newPage);

        var changes = PageChangeType.None;
        double score = 0;
        var regions = new List<DifferenceRegion>();

        // Drop dynamic content (timestamps, page numbers, watermarks) before diffing.
        var oldFiltered = oldPage is null ? null : IgnoreFilter.FilterWords(oldPage, options);
        var newFiltered = newPage is null ? null : IgnoreFilter.FilterWords(newPage, options);

        var visualRegions = new List<DifferenceRegion>();

        if (doText)
        {
            var td = textComparer.ComparePage(oldFiltered, newFiltered, options);
            if (td.Score > 0 || td.Regions.Count > 0)
            {
                changes |= PageChangeType.TextChanged;
                score = Math.Max(score, td.Score);
                regions.AddRange(td.Regions); // added + removed, split per side below
            }
        }

        RenderedPage? oldRender = null, newRender = null;
        if (needRenders)
        {
            oldRender = await renderer.RenderAsync(oldPath, oldNum, options.Dpi, ct);
            newRender = await renderer.RenderAsync(newPath, newNum, options.Dpi, ct);
        }

        if (doVisual && oldRender is not null && newRender is not null)
        {
            var ignorePx = IgnoreFilter.PixelRegions(newNum, newRender, options);
            var img = imageComparer.Compare(oldRender.Png, newRender.Png, options, ignorePx);
            if (img.DifferenceRatio >= options.VisualThreshold && img.DifferentPixels > 0)
            {
                changes |= PageChangeType.VisualChanged;
                score = Math.Max(score, img.DifferenceRatio);
                foreach (var px in img.Regions)
                    visualRegions.Add(new DifferenceRegion(
                        DifferenceKind.Changed,
                        CoordinateConverter.PixelToPoints(px, options.Dpi, newRender.HeightPx)));
                regions.AddRange(visualRegions);
            }
        }

        bool oldBlank = oldRender is not null && blankDetector.IsVisuallyBlank(oldRender, options);
        bool newBlank = newRender is not null && blankDetector.IsVisuallyBlank(newRender, options);
        if (options.DetectBlankPages)
        {
            if (oldBlank && !newBlank) changes |= PageChangeType.WasBlank;
            if (!oldBlank && newBlank) changes |= PageChangeType.BecameBlank;
        }

        if (oldGeom.TryGetValue(oldNum, out var go) && newGeom.TryGetValue(newNum, out var gn)
            && !SameSize(go, gn))
        {
            changes |= PageChangeType.SizeChanged;
        }

        var pageComparison = new PageComparison
        {
            OldPageNumber = oldNum,
            NewPageNumber = newNum,
            Changes = changes,
            DifferenceScore = score,
            OldBlank = oldBlank,
            NewBlank = newBlank,
            Regions = regions,
        };

        DiffSpread? matchedSpread = null;
        if (wantHighlight && changes != PageChangeType.None && (oldRender is not null || newRender is not null))
        {
            // Removed text -> old side; added text -> new side; visual changes -> both.
            var oldRegions = regions.Where(r => r.Kind == DifferenceKind.Removed).Concat(visualRegions).ToList();
            var newRegions = regions.Where(r => r.Kind == DifferenceKind.Added).Concat(visualRegions).ToList();

            matchedSpread = new DiffSpread(
                oldNum, newNum,
                Old: oldRender is null ? null : new HighlightSide(oldRender, oldRegions),
                New: newRender is null ? null : new HighlightSide(newRender, newRegions));
        }

        return (pageComparison, matchedSpread);
    }

    private static bool SameSize(PageGeometry a, PageGeometry b)
    {
        var (aw, ah) = a.Effective;
        var (bw, bh) = b.Effective;
        return Math.Abs(aw - bw) <= SizeTolerancePoints && Math.Abs(ah - bh) <= SizeTolerancePoints;
    }

    private static DifferenceRegion FullPageRegion(RenderedPage page, DifferenceKind kind)
    {
        double widthPt = page.WidthPx * 72.0 / Math.Max(1, page.Dpi);
        double heightPt = page.HeightPx * 72.0 / Math.Max(1, page.Dpi);
        return new DifferenceRegion(kind, new RectangleD(0, 0, widthPt, heightPt));
    }

    private async Task<string> WriteHighlightedAsync(
        string newPath, IReadOnlyList<DiffSpread> spreads, HighlightLayout layout, string artifactDirectory, CancellationToken ct)
    {
        Directory.CreateDirectory(artifactDirectory);
        string outPath = Path.Combine(artifactDirectory, Path.GetFileNameWithoutExtension(newPath) + ".diff.pdf");
        await highlightedPdfWriter.WriteAsync(outPath, spreads, layout, ct);
        return outPath;
    }

    private static string FormatError(DocumentInfo oldInfo, DocumentInfo newInfo)
    {
        var parts = new List<string>();
        if (!oldInfo.IsComparable) parts.Add($"old: {oldInfo.Status} ({oldInfo.Message})");
        if (!newInfo.IsComparable) parts.Add($"new: {newInfo.Status} ({newInfo.Message})");
        return string.Join("; ", parts);
    }
}
