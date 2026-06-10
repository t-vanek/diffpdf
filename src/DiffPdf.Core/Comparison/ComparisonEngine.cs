using System.Diagnostics;
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
    IHighlightedPdfWriterFactory highlightedPdfWriterFactory,
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
        // One document open per side covers probe + extraction, and the two sides run concurrently. An
        // unreadable pair pays one extraction it won't use (the rare error path); every healthy pair saves
        // a second open + page pass per side — decisive for UNC inputs.
        long extractStart = Stopwatch.GetTimestamp();
        var oldExtractTask = textExtractor.ProbeAndExtractAsync(oldPath, options.Pages, ct);
        var newExtractTask = textExtractor.ProbeAndExtractAsync(newPath, options.Pages, ct);
        await Task.WhenAll(oldExtractTask, newExtractTask);
        var (oldInfo, oldText) = oldExtractTask.Result;
        var (newInfo, newText) = newExtractTask.Result;
        EngineMetrics.Record(EngineMetrics.Extract, extractStart);

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

        // Page pairs are independent: run a bounded number concurrently (renders stay capped by the process-wide
        // IPdfWorkLimiter). Each result lands in its slot, so page order — and the spread sequence in the diff
        // PDF — is preserved no matter which page finishes first.
        var results = new (PageComparison Comparison, DiffSpread? Spread, ContentError? Error)[pairs.Count];
        var parallelism = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Clamp(options.MaxParallelPages, 1, 8),
            CancellationToken = ct,
        };
        await Parallel.ForEachAsync(Enumerable.Range(0, pairs.Count), parallelism, async (i, token) =>
            results[i] = await ComparePairContainedAsync(
                pairs[i], oldPath, newPath, oldByNum, newByNum, oldGeom, newGeom, options, artifactDirectory is not null, token));

        foreach (var (comparison, spread, error) in results)
        {
            pageComparisons.Add(comparison);
            if (spread is not null) spreads.Add(spread);
            if (error is not null) contentErrors.Add(error);
        }

        string? highlightedPath = null;
        if (options.ProduceHighlightedPdf && artifactDirectory is not null && spreads.Count > 0)
        {
            try
            {
                highlightedPath = await WriteHighlightedAsync(newPath, spreads, options, artifactDirectory, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Cancellation here is the per-pair timeout or a host shutdown — NOT a failure to produce the
                // optional diff PDF — so it must propagate, not be swallowed by the catch below. The worker
                // turns a timeout into a per-file error and, on shutdown, leaves the pair Running to retry.
                // Swallowing it would return Outcome.Compared and record the pair as a successful comparison
                // silently missing its highlighted diff (and skip the retry the cancellation sweep relies on).
                throw;
            }
            catch (Exception ex)
            {
                // A genuine failure to render the optional diff PDF (malformed page, PdfSharp/IO error) does not
                // invalidate the comparison itself — keep the result, just without the highlight artifact.
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

    /// <summary>
    /// Runs one page-pair comparison with per-page error containment: a page that fails to render/extract/compare
    /// is flagged as a <see cref="PageChangeType.ProcessingError"/> (plus an engine content error) instead of
    /// failing the whole file. Cancellation propagates — the worker turns it into a retry or a per-pair timeout.
    /// </summary>
    private async Task<(PageComparison Comparison, DiffSpread? Spread, ContentError? Error)> ComparePairContainedAsync(
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
        try
        {
            var (comparison, spread) = await ComparePairAsync(
                pair, oldPath, newPath, oldByNum, newByNum, oldGeom, newGeom, options, wantHighlight, ct);
            return (comparison, spread, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Host shutdown / per-pair timeout — abort so the worker can retry or record the whole pair.
            throw;
        }
        catch (Exception ex)
        {
            // One page failed to render/extract/compare (corrupt page, undecodable render). Flag THAT page and
            // keep comparing the rest, instead of failing the whole file. The page shows as a processing error
            // and is recorded as a content error so it surfaces in the report and the client.
            int page = pair.NewPageNumber ?? pair.OldPageNumber ?? 0;
            logger.LogWarning(ex, "Page {Page} of {New} failed to compare; flagging as a processing error and continuing.", page, newPath);
            return (
                new PageComparison
                {
                    OldPageNumber = pair.OldPageNumber,
                    NewPageNumber = pair.NewPageNumber,
                    Changes = PageChangeType.ProcessingError,
                    DifferenceScore = 1.0,
                },
                null,
                new ContentError(ContentErrorSide.Engine, page, "processing", Truncate(ex.Message)));
        }
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
        // The vector writer overlays on the original PDF, so highlighting needs no render.
        bool rasterHighlight = wantHighlight && options.HighlightStyle == HighlightStyle.Raster;
        bool needRenders = doVisual || options.DetectBlankPages || rasterHighlight;

        // --- Page added (only in new) ---
        if (pair.OldPageNumber is null && pair.NewPageNumber is int addedNum)
        {
            RenderedPage? render = needRenders ? await renderer.RenderAsync(newPath, addedNum, options.Dpi, ct) : null;
            bool blank = render is not null && DetectBlankTimed(render, options);
            var comparison = new PageComparison
            {
                NewPageNumber = addedNum,
                Changes = PageChangeType.PageAdded,
                DifferenceScore = 1.0,
                NewBlank = blank,
            };
            var spread = wantHighlight
                ? new DiffSpread(null, addedNum, Old: null,
                    New: new HighlightSide(newPath, addedNum, [FullPageRegion(newGeom, addedNum, DifferenceKind.Added)], render))
                : null;
            return (comparison, spread);
        }

        // --- Page removed (only in old) ---
        if (pair.NewPageNumber is null && pair.OldPageNumber is int removedNum)
        {
            RenderedPage? render = needRenders ? await renderer.RenderAsync(oldPath, removedNum, options.Dpi, ct) : null;
            bool blank = render is not null && DetectBlankTimed(render, options);
            var comparison = new PageComparison
            {
                OldPageNumber = removedNum,
                Changes = PageChangeType.PageRemoved,
                DifferenceScore = 1.0,
                OldBlank = blank,
            };
            var spread = wantHighlight
                ? new DiffSpread(removedNum, null,
                    Old: new HighlightSide(oldPath, removedNum, [FullPageRegion(oldGeom, removedNum, DifferenceKind.Removed)], render), New: null)
                : null;
            return (comparison, spread);
        }

        // --- Matched pair ---
        int oldNum = pair.OldPageNumber!.Value;
        int newNum = pair.NewPageNumber!.Value;
        oldByNum.TryGetValue(oldNum, out var oldPage);
        newByNum.TryGetValue(newNum, out var newPage);

        // A page whose text could not be extracted can't be text-diffed — surface it as a processing error (the
        // engine loop catches this and flags the page) rather than silently diffing empty-vs-content text.
        if (doText && (oldPage?.ExtractionFailed == true || newPage?.ExtractionFailed == true))
            throw new InvalidOperationException($"Text extraction failed for page {newNum}.");

        var changes = PageChangeType.None;
        double score = 0;
        var regions = new List<DifferenceRegion>();

        // Drop dynamic content (timestamps, page numbers, watermarks) before diffing.
        var oldFiltered = oldPage is null ? null : IgnoreFilter.FilterWords(oldPage, options);
        var newFiltered = newPage is null ? null : IgnoreFilter.FilterWords(newPage, options);

        var visualRegions = new List<DifferenceRegion>();

        if (doText)
        {
            long textStart = Stopwatch.GetTimestamp();
            var td = textComparer.ComparePage(oldFiltered, newFiltered, options);
            EngineMetrics.Record(EngineMetrics.TextDiff, textStart);
            if (td.Score > 0 && td.Score >= options.EffectiveTextDifferenceThreshold)
            {
                changes |= PageChangeType.TextChanged;
                score = Math.Max(score, td.Score);
                regions.AddRange(td.Regions); // added + removed, split per side below
            }
        }

        RenderedPage? oldRender = null, newRender = null;
        if (needRenders)
        {
            // The two sides are independent and IPdfWorkLimiter already bounds process-wide render
            // concurrency, so rendering them concurrently halves the render wall-clock of a page pair.
            var oldRenderTask = renderer.RenderAsync(oldPath, oldNum, options.Dpi, ct);
            var newRenderTask = renderer.RenderAsync(newPath, newNum, options.Dpi, ct);
            await Task.WhenAll(oldRenderTask, newRenderTask);
            oldRender = oldRenderTask.Result;
            newRender = newRenderTask.Result;
        }

        bool oldBlank = false, newBlank = false;
        bool blanksFromDiff = false;

        if (doVisual && oldRender is not null && newRender is not null)
        {
            var ignorePx = IgnoreFilter.PixelRegions(newNum, newRender, options);
            long pixelStart = Stopwatch.GetTimestamp();
            var img = imageComparer.Compare(oldRender.Png, newRender.Png, options, ignorePx);
            EngineMetrics.Record(EngineMetrics.PixelCompare, pixelStart);

            // The comparer evaluates blankness on the bitmaps it already decoded for the diff —
            // reuse that instead of paying two more PNG decodes in the standalone blank detector.
            (oldBlank, newBlank, blanksFromDiff) = (img.OldBlank, img.NewBlank, true);

            if (img.DifferenceRatio >= options.EffectiveVisualThreshold && img.DifferentPixels > 0)
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

        if (!blanksFromDiff)
        {
            oldBlank = oldRender is not null && DetectBlankTimed(oldRender, options);
            newBlank = newRender is not null && DetectBlankTimed(newRender, options);
        }
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
        if (wantHighlight && changes != PageChangeType.None)
        {
            // Removed text -> old side; added text -> new side; visual changes -> both.
            var oldRegions = regions.Where(r => r.Kind == DifferenceKind.Removed).Concat(visualRegions).ToList();
            var newRegions = regions.Where(r => r.Kind == DifferenceKind.Added).Concat(visualRegions).ToList();

            matchedSpread = new DiffSpread(
                oldNum, newNum,
                Old: new HighlightSide(oldPath, oldNum, oldRegions, oldRender),
                New: new HighlightSide(newPath, newNum, newRegions, newRender));
        }

        return (pageComparison, matchedSpread);
    }

    private bool DetectBlankTimed(RenderedPage render, ComparisonOptions options)
    {
        long start = Stopwatch.GetTimestamp();
        bool blank = blankDetector.IsVisuallyBlank(render, options);
        EngineMetrics.Record(EngineMetrics.BlankDetect, start);
        return blank;
    }

    private static bool SameSize(PageGeometry a, PageGeometry b)
    {
        var (aw, ah) = a.Effective;
        var (bw, bh) = b.Effective;
        return Math.Abs(aw - bw) <= SizeTolerancePoints && Math.Abs(ah - bh) <= SizeTolerancePoints;
    }

    private static DifferenceRegion FullPageRegion(IReadOnlyDictionary<int, PageGeometry> geom, int pageNumber, DifferenceKind kind)
    {
        double widthPt = geom.TryGetValue(pageNumber, out var g) ? g.Width : 612;
        double heightPt = g?.Height ?? 792;
        return new DifferenceRegion(kind, new RectangleD(0, 0, widthPt, heightPt));
    }

    private async Task<string> WriteHighlightedAsync(
        string newPath, IReadOnlyList<DiffSpread> spreads, ComparisonOptions options, string artifactDirectory, CancellationToken ct)
    {
        Directory.CreateDirectory(artifactDirectory);
        string outPath = Path.Combine(artifactDirectory, Path.GetFileNameWithoutExtension(newPath) + ".diff.pdf");
        // Write to a temp sibling and atomically move it into place, so a failed or cancelled write never leaves a
        // half-written .diff.pdf that could be mistaken for a valid diff.
        string tmpPath = outPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var writer = highlightedPdfWriterFactory.Get(options.HighlightStyle);
        long writeStart = Stopwatch.GetTimestamp();
        try
        {
            await writer.WriteAsync(tmpPath, spreads, options.HighlightLayout, ct);
            File.Move(tmpPath, outPath, overwrite: true);
            EngineMetrics.Record(EngineMetrics.HighlightWrite, writeStart);
            return outPath;
        }
        catch
        {
            try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { /* best-effort temp cleanup */ }
            throw;
        }
    }

    private static string Truncate(string s, int max = 300) =>
        string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s[..max] + "…";

    private static string FormatError(DocumentInfo oldInfo, DocumentInfo newInfo)
    {
        var parts = new List<string>();
        if (!oldInfo.IsComparable) parts.Add($"old: {oldInfo.Status} ({oldInfo.Message})");
        if (!newInfo.IsComparable) parts.Add($"new: {newInfo.Status} ({newInfo.Message})");
        return string.Join("; ", parts);
    }
}
