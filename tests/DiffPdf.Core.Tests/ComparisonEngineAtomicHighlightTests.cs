using DiffPdf.Core.Abstractions;
using DiffPdf.Core.Comparison;
using DiffPdf.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiffPdf.Core.Tests;

/// <summary>
/// The highlighted diff PDF is written atomically (temp + move), so a failed/cancelled write never leaves a
/// half-written named <c>.diff.pdf</c> on disk, and a successful write produces exactly the named artifact.
/// </summary>
public class ComparisonEngineAtomicHighlightTests
{
    private sealed class AddedPageAligner : IPageAligner
    {
        public IReadOnlyList<AlignedPagePair> Align(IReadOnlyList<PageText> o, IReadOnlyList<PageText> n, ComparisonOptions options) => [new(null, 1)];
    }
    private sealed class StubExtractor : IPdfTextExtractor
    {
        public DocumentInfo Probe(string path) => new() { Status = DocumentStatus.Ok, PageCount = 1 };
        public Task<IReadOnlyList<PageText>> ExtractAsync(string path, PageRange range, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<PageText>>([]);
    }
    private sealed class NoRenderFactory : IPdfPageRendererFactory
    {
        public IPdfPageRenderer GetRenderer(RendererBackend backend) => new R();
        private sealed class R : IPdfPageRenderer
        {
            public RendererBackend Backend => RendererBackend.Pdfium;
            public int GetPageCount(string path) => 1;
            public Task<RenderedPage> RenderAsync(string path, int pageNumber, int dpi, CancellationToken ct = default) => throw new NotSupportedException();
        }
    }
    private sealed class Unused : ITextComparer, IImageComparer, IBlankPageDetector, IContentErrorDetector
    {
        public TextPageDiff ComparePage(PageText? o, PageText? n, ComparisonOptions options) => throw new NotSupportedException();
        public double Similarity(PageText o, PageText n) => throw new NotSupportedException();
        public ImageDiffResult Compare(byte[] o, byte[] n, ComparisonOptions options, IReadOnlyList<RectangleD>? ig = null) => throw new NotSupportedException();
        public bool IsVisuallyBlank(RenderedPage page, ComparisonOptions options) => throw new NotSupportedException();
        public IReadOnlyList<ContentError> Detect(IReadOnlyList<PageText> pages, ContentErrorSide side, ComparisonOptions options) => throw new NotSupportedException();
    }

    private sealed class PartialThenFailWriter : IHighlightedPdfWriter
    {
        public HighlightStyle Style => HighlightStyle.VectorOverlay;
        public async Task WriteAsync(string outputPath, IReadOnlyList<DiffSpread> spreads, HighlightLayout layout, CancellationToken ct = default)
        {
            await File.WriteAllTextAsync(outputPath, "partial diff", ct); // writes the TEMP path the engine hands it
            throw new InvalidOperationException("boom after a partial write");
        }
    }
    private sealed class SuccessWriter : IHighlightedPdfWriter
    {
        public HighlightStyle Style => HighlightStyle.VectorOverlay;
        public Task WriteAsync(string outputPath, IReadOnlyList<DiffSpread> spreads, HighlightLayout layout, CancellationToken ct = default) =>
            File.WriteAllTextAsync(outputPath, "diff", ct);
    }
    private sealed class WriterFactory(IHighlightedPdfWriter w) : IHighlightedPdfWriterFactory
    {
        public IHighlightedPdfWriter Get(HighlightStyle style) => w;
    }

    private static ComparisonEngine Engine(IHighlightedPdfWriter writer)
    {
        var u = new Unused();
        return new ComparisonEngine(new StubExtractor(), u, u, u, new AddedPageAligner(), u, new NoRenderFactory(), new WriterFactory(writer), NullLogger<ComparisonEngine>.Instance);
    }

    private static ComparisonOptions Options() => new()
    {
        Mode = ComparisonMode.Text, DetectBlankPages = false, DetectContentErrors = false,
        ProduceHighlightedPdf = true, HighlightStyle = HighlightStyle.VectorOverlay,
    };

    [Fact]
    public async Task A_failed_highlight_write_leaves_no_partial_or_temp_artifact()
    {
        string dir = Path.Combine(Path.GetTempPath(), "DiffPdfAtomicTest", Guid.NewGuid().ToString("N"));
        try
        {
            var result = await Engine(new PartialThenFailWriter()).CompareAsync("/old/a.pdf", "/new/a.pdf", Options(), dir, CancellationToken.None);

            Assert.Equal(ComparisonOutcome.Compared, result.Outcome); // comparison stands; only the optional artifact failed
            Assert.Null(result.HighlightedPdfPath);
            Assert.Empty(Directory.GetFiles(dir, "*.diff.pdf")); // no half-written named artifact
            Assert.Empty(Directory.GetFiles(dir, "*.tmp"));       // temp cleaned up
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task A_successful_highlight_write_produces_exactly_the_named_artifact()
    {
        string dir = Path.Combine(Path.GetTempPath(), "DiffPdfAtomicTest", Guid.NewGuid().ToString("N"));
        try
        {
            var result = await Engine(new SuccessWriter()).CompareAsync("/old/a.pdf", "/new/a.pdf", Options(), dir, CancellationToken.None);

            Assert.NotNull(result.HighlightedPdfPath);
            Assert.Single(Directory.GetFiles(dir, "*.diff.pdf"));
            Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }
}
