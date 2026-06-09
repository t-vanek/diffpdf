using DiffPdf.Core.Abstractions;
using DiffPdf.Core.Comparison;
using DiffPdf.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiffPdf.Core.Tests;

/// <summary>
/// A single page that fails to render / extract / compare must be contained — flagged as a
/// <see cref="PageChangeType.ProcessingError"/> + a <see cref="ContentErrorSide.Engine"/> content error — so the
/// rest of the document still compares, instead of aborting the whole file. Cancellation still propagates.
/// </summary>
public class ComparisonEnginePerPageResilienceTests
{
    private sealed class ThreeMatchedAligner : IPageAligner
    {
        public IReadOnlyList<AlignedPagePair> Align(IReadOnlyList<PageText> o, IReadOnlyList<PageText> n, ComparisonOptions options) =>
            [new(1, 1), new(2, 2), new(3, 3)];
    }

    private sealed class ThreePageExtractor(bool failPage2Extraction = false) : IPdfTextExtractor
    {
        private static readonly PageGeometry[] Geom = [new(1, 600, 800, 0), new(2, 600, 800, 0), new(3, 600, 800, 0)];
        public DocumentInfo Probe(string path) => new() { Status = DocumentStatus.Ok, PageCount = 3, Pages = Geom };
        public Task<IReadOnlyList<PageText>> ExtractAsync(string path, PageRange range, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<PageText>>(
            [
                new PageText { PageNumber = 1, Width = 600, Height = 800 },
                new PageText { PageNumber = 2, Width = 600, Height = 800, ExtractionFailed = failPage2Extraction },
                new PageText { PageNumber = 3, Width = 600, Height = 800 },
            ]);
    }

    private sealed class RendererFailingOnPage(int badPage) : IPdfPageRenderer
    {
        public RendererBackend Backend => RendererBackend.Pdfium;
        public int GetPageCount(string path) => 3;
        public Task<RenderedPage> RenderAsync(string path, int pageNumber, int dpi, CancellationToken ct = default) =>
            pageNumber == badPage
                ? throw new InvalidOperationException($"render boom on page {pageNumber}")
                : Task.FromResult(new RenderedPage { PageNumber = pageNumber, Dpi = dpi, WidthPx = 10, HeightPx = 10, Png = [0, 0, 0, 0] });
    }

    private sealed class CancellingRendererOnPage(int badPage, CancellationTokenSource cts) : IPdfPageRenderer
    {
        public RendererBackend Backend => RendererBackend.Pdfium;
        public int GetPageCount(string path) => 3;
        public Task<RenderedPage> RenderAsync(string path, int pageNumber, int dpi, CancellationToken ct = default)
        {
            if (pageNumber == badPage) { cts.Cancel(); ct.ThrowIfCancellationRequested(); }
            return Task.FromResult(new RenderedPage { PageNumber = pageNumber, Dpi = dpi, WidthPx = 10, HeightPx = 10, Png = [0, 0, 0, 0] });
        }
    }

    private sealed class RendererFactory(IPdfPageRenderer r) : IPdfPageRendererFactory
    {
        public IPdfPageRenderer GetRenderer(RendererBackend backend) => r;
    }

    private sealed class NoDiffTextComparer : ITextComparer
    {
        public TextPageDiff ComparePage(PageText? o, PageText? n, ComparisonOptions options) => TextPageDiff.Identical;
        public double Similarity(PageText o, PageText n) => 1.0;
    }
    private sealed class ThrowingTextComparer : ITextComparer
    {
        public TextPageDiff ComparePage(PageText? o, PageText? n, ComparisonOptions options) => throw new NotSupportedException();
        public double Similarity(PageText o, PageText n) => 1.0;
    }
    private sealed class FalseBlankDetector : IBlankPageDetector
    {
        public bool IsVisuallyBlank(RenderedPage page, ComparisonOptions options) => false;
    }
    private sealed class ThrowingBlankDetector : IBlankPageDetector
    {
        public bool IsVisuallyBlank(RenderedPage page, ComparisonOptions options) => throw new NotSupportedException();
    }
    private sealed class ThrowingImageComparer : IImageComparer
    {
        public ImageDiffResult Compare(byte[] o, byte[] n, ComparisonOptions options, IReadOnlyList<RectangleD>? ig = null) => throw new NotSupportedException();
    }
    private sealed class NoContentErrors : IContentErrorDetector
    {
        public IReadOnlyList<ContentError> Detect(IReadOnlyList<PageText> pages, ContentErrorSide side, ComparisonOptions options) => [];
    }
    private sealed class NoopWriterFactory : IHighlightedPdfWriterFactory
    {
        public IHighlightedPdfWriter Get(HighlightStyle style) => throw new NotSupportedException();
    }

    private static ComparisonEngine Engine(IPdfTextExtractor extractor, IPdfPageRenderer renderer, ITextComparer text, IBlankPageDetector blank) => new(
        extractor, text, new ThrowingImageComparer(), blank,
        new ThreeMatchedAligner(), new NoContentErrors(), new RendererFactory(renderer),
        new NoopWriterFactory(), NullLogger<ComparisonEngine>.Instance);

    [Fact]
    public async Task A_render_failure_on_one_page_is_contained_and_the_rest_compare()
    {
        // Mode=None + blank detection renders every page but runs no text/image diff; page 2's render throws.
        var engine = Engine(new ThreePageExtractor(), new RendererFailingOnPage(2), new ThrowingTextComparer(), new FalseBlankDetector());
        var opts = new ComparisonOptions { Mode = ComparisonMode.None, DetectBlankPages = true, DetectContentErrors = false, ProduceHighlightedPdf = false };

        var result = await engine.CompareAsync("/old.pdf", "/new.pdf", opts, artifactDirectory: null, CancellationToken.None);

        Assert.Equal(ComparisonOutcome.Compared, result.Outcome);                  // not aborted
        Assert.Equal(3, result.Pages.Count);                                       // every page produced a result
        var bad = Assert.Single(result.Pages, p => p.Changes.HasFlag(PageChangeType.ProcessingError));
        Assert.Equal(2, bad.DisplayPageNumber);
        Assert.Equal(2, result.Pages.Count(p => !p.IsDifferent));                  // pages 1 & 3 compared cleanly
        var ce = Assert.Single(result.ContentErrors);
        Assert.Equal(ContentErrorSide.Engine, ce.Side);
        Assert.Equal(2, ce.PageNumber);
        Assert.True(result.HasContentErrors);
    }

    [Fact]
    public async Task An_extraction_failure_on_one_page_is_contained_and_the_rest_compare()
    {
        // Text mode: page 2's text could not be extracted (flagged) → ProcessingError; no render needed.
        var engine = Engine(new ThreePageExtractor(failPage2Extraction: true), new RendererFailingOnPage(99), new NoDiffTextComparer(), new ThrowingBlankDetector());
        var opts = new ComparisonOptions { Mode = ComparisonMode.Text, DetectBlankPages = false, DetectContentErrors = false, ProduceHighlightedPdf = false };

        var result = await engine.CompareAsync("/old.pdf", "/new.pdf", opts, artifactDirectory: null, CancellationToken.None);

        Assert.Equal(ComparisonOutcome.Compared, result.Outcome);
        var bad = Assert.Single(result.Pages, p => p.Changes.HasFlag(PageChangeType.ProcessingError));
        Assert.Equal(2, bad.DisplayPageNumber);
        Assert.Equal(ContentErrorSide.Engine, Assert.Single(result.ContentErrors).Side);
    }

    [Fact]
    public async Task Cancellation_during_a_page_propagates_and_is_not_swallowed_as_a_page_error()
    {
        using var cts = new CancellationTokenSource();
        var engine = Engine(new ThreePageExtractor(), new CancellingRendererOnPage(2, cts), new ThrowingTextComparer(), new FalseBlankDetector());
        var opts = new ComparisonOptions { Mode = ComparisonMode.None, DetectBlankPages = true, DetectContentErrors = false, ProduceHighlightedPdf = false };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            engine.CompareAsync("/old.pdf", "/new.pdf", opts, artifactDirectory: null, cts.Token));
    }
}
