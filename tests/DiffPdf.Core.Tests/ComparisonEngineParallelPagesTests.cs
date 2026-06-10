using DiffPdf.Core.Abstractions;
using DiffPdf.Core.Comparison;
using DiffPdf.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiffPdf.Core.Tests;

/// <summary>
/// With <see cref="ComparisonOptions.MaxParallelPages"/> &gt; 1 page pairs run concurrently — the result
/// order (and thus the spread sequence in the diff PDF) must still follow the aligned page order, not the
/// completion order. The fake renderer finishes the LAST page first to force out-of-order completion.
/// </summary>
public class ComparisonEngineParallelPagesTests
{
    private const int PageCount = 5;

    private sealed class MatchedAligner : IPageAligner
    {
        public IReadOnlyList<AlignedPagePair> Align(IReadOnlyList<PageText> o, IReadOnlyList<PageText> n, ComparisonOptions options) =>
            [.. Enumerable.Range(1, PageCount).Select(p => new AlignedPagePair(p, p))];
    }

    private sealed class Extractor : IPdfTextExtractor
    {
        private static readonly PageGeometry[] Geom =
            [.. Enumerable.Range(1, PageCount).Select(p => new PageGeometry(p, 600, 800, 0))];

        public DocumentInfo Probe(string path) => new() { Status = DocumentStatus.Ok, PageCount = PageCount, Pages = Geom };

        public Task<IReadOnlyList<PageText>> ExtractAsync(string path, PageRange range, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<PageText>>(
                [.. Enumerable.Range(1, PageCount).Select(p => new PageText { PageNumber = p, Width = 600, Height = 800 })]);
    }

    /// <summary>Finishes page 5 first and page 1 last, so append-as-completed would reverse the order.</summary>
    private sealed class ReverseStaggeredRenderer : IPdfPageRenderer
    {
        public RendererBackend Backend => RendererBackend.Pdfium;
        public int GetPageCount(string path) => PageCount;

        public async Task<RenderedPage> RenderAsync(string path, int pageNumber, int dpi, CancellationToken ct = default)
        {
            await Task.Delay((PageCount + 1 - pageNumber) * 25, ct);
            return new RenderedPage { PageNumber = pageNumber, Dpi = dpi, WidthPx = 10, HeightPx = 10, Png = [1] };
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

    private sealed class FalseBlankDetector : IBlankPageDetector
    {
        public bool IsVisuallyBlank(RenderedPage page, ComparisonOptions options) => false;
    }

    private sealed class ThrowingImageComparer : IImageComparer
    {
        public ImageDiffResult Compare(byte[] o, byte[] n, ComparisonOptions options, IReadOnlyList<RectangleD>? ig = null) =>
            throw new NotSupportedException();
    }

    private sealed class NoContentErrors : IContentErrorDetector
    {
        public IReadOnlyList<ContentError> Detect(IReadOnlyList<PageText> pages, ContentErrorSide side, ComparisonOptions options) => [];
    }

    private sealed class NoopWriterFactory : IHighlightedPdfWriterFactory
    {
        public IHighlightedPdfWriter Get(HighlightStyle style) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Pages_run_in_parallel_but_results_keep_page_order()
    {
        var engine = new ComparisonEngine(
            new Extractor(), new NoDiffTextComparer(), new ThrowingImageComparer(), new FalseBlankDetector(),
            new MatchedAligner(), new NoContentErrors(), new RendererFactory(new ReverseStaggeredRenderer()),
            new NoopWriterFactory(), NullLogger<ComparisonEngine>.Instance);

        // Mode=None + blank detection forces a render per page (no text/image diff); 4 pages in flight at once.
        var opts = new ComparisonOptions
        {
            Mode = ComparisonMode.None,
            DetectBlankPages = true,
            DetectContentErrors = false,
            ProduceHighlightedPdf = false,
            MaxParallelPages = 4,
        };

        var result = await engine.CompareAsync("/old.pdf", "/new.pdf", opts, artifactDirectory: null, CancellationToken.None);

        Assert.Equal(ComparisonOutcome.Compared, result.Outcome);
        Assert.Equal(PageCount, result.Pages.Count);
        Assert.Equal(
            Enumerable.Range(1, PageCount),
            result.Pages.Select(p => p.NewPageNumber ?? 0));
        Assert.Empty(result.ContentErrors);
    }
}
