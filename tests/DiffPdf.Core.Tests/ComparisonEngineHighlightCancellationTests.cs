using DiffPdf.Core.Abstractions;
using DiffPdf.Core.Comparison;
using DiffPdf.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiffPdf.Core.Tests;

/// <summary>
/// The optional highlighted-diff-PDF step in <see cref="ComparisonEngine"/> must not swallow cancellation.
/// The engine runs under a per-pair timeout linked to the host shutdown token (see CompareFilePairHandler):
/// a cancellation landing while the diff PDF is being written is a timeout or a shutdown, NOT a "failed to
/// render the optional artifact" — so it must propagate (worker → timeout error, or leave the pair Running
/// to retry), never be caught as a warning that returns a successful, silently-highlight-less result.
/// A genuine writer failure (malformed page, PdfSharp/IO error) is still swallowed: the comparison stands.
/// </summary>
public class ComparisonEngineHighlightCancellationTests
{
    // --- One added page → the engine produces exactly one highlight spread, so the write step runs. ---
    private sealed class AddedPageAligner : IPageAligner
    {
        public IReadOnlyList<AlignedPagePair> Align(IReadOnlyList<PageText> oldPages, IReadOnlyList<PageText> newPages, ComparisonOptions options) =>
            [new AlignedPagePair(null, 1)];
    }

    private sealed class StubExtractor : IPdfTextExtractor
    {
        public DocumentInfo Probe(string path) => new() { Status = DocumentStatus.Ok, PageCount = 1 };
        public Task<IReadOnlyList<PageText>> ExtractAsync(string path, PageRange range, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<PageText>>([]);
    }

    // GetRenderer is called unconditionally, but a text-only/no-blank/vector-overlay run never renders.
    private sealed class StubRenderer : IPdfPageRenderer
    {
        public RendererBackend Backend => RendererBackend.Pdfium;
        public int GetPageCount(string path) => 1;
        public Task<RenderedPage> RenderAsync(string path, int pageNumber, int dpi, CancellationToken ct = default) =>
            throw new NotSupportedException("no render expected on the text-only highlight path");
    }
    private sealed class StubRendererFactory : IPdfPageRendererFactory
    {
        public IPdfPageRenderer GetRenderer(RendererBackend backend) => new StubRenderer();
    }

    /// <summary>Simulates the real RasterHighlightPdfWriter: a timeout/shutdown cancels the token mid-write.</summary>
    private sealed class CancellingWriter(CancellationTokenSource cts) : IHighlightedPdfWriter
    {
        public HighlightStyle Style => HighlightStyle.VectorOverlay;
        public Task WriteAsync(string outputPath, IReadOnlyList<DiffSpread> spreads, HighlightLayout layout, CancellationToken ct = default)
        {
            cts.Cancel();                       // the per-pair timeout / host shutdown fires right now
            ct.ThrowIfCancellationRequested();  // exactly what RasterHighlightPdfWriter does per spread
            return Task.CompletedTask;
        }
    }

    /// <summary>A genuine, non-cancellation failure to produce the optional artifact.</summary>
    private sealed class FailingWriter : IHighlightedPdfWriter
    {
        public HighlightStyle Style => HighlightStyle.VectorOverlay;
        public Task WriteAsync(string outputPath, IReadOnlyList<DiffSpread> spreads, HighlightLayout layout, CancellationToken ct = default) =>
            throw new InvalidOperationException("malformed page");
    }

    private sealed class WriterFactory(IHighlightedPdfWriter writer) : IHighlightedPdfWriterFactory
    {
        public IHighlightedPdfWriter Get(HighlightStyle style) => writer;
    }

    // Collaborators the text-only / page-added path must never touch — throw if it does.
    private sealed class UnusedTextComparer : ITextComparer
    {
        public TextPageDiff ComparePage(PageText? oldPage, PageText? newPage, ComparisonOptions options) => throw new NotSupportedException();
        public double Similarity(PageText oldPage, PageText newPage) => throw new NotSupportedException();
    }
    private sealed class UnusedImageComparer : IImageComparer
    {
        public ImageDiffResult Compare(byte[] oldPng, byte[] newPng, ComparisonOptions options, IReadOnlyList<RectangleD>? ignoreRegionsPx = null) => throw new NotSupportedException();
    }
    private sealed class UnusedBlankDetector : IBlankPageDetector
    {
        public bool IsVisuallyBlank(RenderedPage page, ComparisonOptions options) => throw new NotSupportedException();
    }
    private sealed class UnusedContentErrorDetector : IContentErrorDetector
    {
        public IReadOnlyList<ContentError> Detect(IReadOnlyList<PageText> pages, ContentErrorSide side, ComparisonOptions options) => throw new NotSupportedException();
    }

    private static ComparisonEngine Engine(IHighlightedPdfWriter writer) => new(
        new StubExtractor(), new UnusedTextComparer(), new UnusedImageComparer(), new UnusedBlankDetector(),
        new AddedPageAligner(), new UnusedContentErrorDetector(), new StubRendererFactory(),
        new WriterFactory(writer), NullLogger<ComparisonEngine>.Instance);

    // Text-only + vector-overlay + no blank detection ⇒ no rendering needed, but a highlight IS produced.
    private static ComparisonOptions Options() => new()
    {
        Mode = ComparisonMode.Text,
        DetectBlankPages = false,
        DetectContentErrors = false,
        ProduceHighlightedPdf = true,
        HighlightStyle = HighlightStyle.VectorOverlay,
    };

    [Fact]
    public async Task Cancellation_during_highlight_write_propagates_instead_of_recording_a_compared_result()
    {
        using var cts = new CancellationTokenSource();
        var engine = Engine(new CancellingWriter(cts));

        // The page loop completes with the token live; cancellation lands in the writer. It must surface as
        // OperationCanceledException — swallowing it would return Outcome.Compared with a null highlight, and
        // the worker would finalize the pair instead of retrying it.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            engine.CompareAsync("/old/a.pdf", "/new/a.pdf", Options(), artifactDirectory: Path.GetTempPath(), cts.Token));
    }

    [Fact]
    public async Task Genuine_highlight_write_failure_is_swallowed_and_comparison_still_succeeds()
    {
        var engine = Engine(new FailingWriter());

        var result = await engine.CompareAsync(
            "/old/a.pdf", "/new/a.pdf", Options(), artifactDirectory: Path.GetTempPath(), CancellationToken.None);

        Assert.Equal(ComparisonOutcome.Compared, result.Outcome); // the comparison itself is valid…
        Assert.Null(result.HighlightedPdfPath);                   // …only the optional artifact is missing
        Assert.Equal(1, result.DifferingPages);                   // the added page was still reported
    }
}
