using DiffPdf.Pdf.Rendering;

namespace DiffPdf.Core.Tests;

/// <summary>
/// <see cref="GhostscriptPageRenderer.ProducedPages"/> maps block-render output files to document pages by
/// suffix ORDER, not value — correct for gs's output-sequence numbering (1, 2, …) AND for a hypothetical
/// document-page numbering, and robust to a truncated tail or unsubstituted patterns.
/// </summary>
public sealed class GhostscriptBlockOutputMappingTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("diffpdf-gsblock-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort temp cleanup */ }
    }

    private string Prefix => Path.Combine(_dir, "render");

    private void Touch(string name) => File.WriteAllBytes(Path.Combine(_dir, name), [1]);

    [Fact]
    public void OutputSequenceNumbering_MapsToDocumentPages()
    {
        // gs renders pages 9-11 of the document but numbers files 001-003 (output sequence).
        Touch("render_001.png");
        Touch("render_002.png");
        Touch("render_003.png");

        var pages = GhostscriptPageRenderer.ProducedPages(Prefix, firstPage: 9);

        Assert.Equal([9, 10, 11], pages.Select(p => p.Page));
        Assert.EndsWith("render_001.png", pages[0].File);
        Assert.EndsWith("render_003.png", pages[2].File);
    }

    [Fact]
    public void DocumentPageNumbering_MapsIdentically()
    {
        // A build that numbered by document page (009-011) maps the same way thanks to ordered mapping.
        Touch("render_009.png");
        Touch("render_010.png");
        Touch("render_011.png");

        var pages = GhostscriptPageRenderer.ProducedPages(Prefix, firstPage: 9);

        Assert.Equal([9, 10, 11], pages.Select(p => p.Page));
        Assert.EndsWith("render_009.png", pages[0].File);
    }

    [Fact]
    public void TruncatedTail_MapsOnlyTheProducedPages()
    {
        // The document ended at page 10 → an 8-page block starting at 9 produced just two files.
        Touch("render_001.png");
        Touch("render_002.png");

        var pages = GhostscriptPageRenderer.ProducedPages(Prefix, firstPage: 9);

        Assert.Equal([9, 10], pages.Select(p => p.Page));
    }

    [Fact]
    public void NonNumericSuffixes_AreIgnored()
    {
        // An unsubstituted pattern (or stray file) must not be mistaken for a rendered page.
        Touch("render_%03d.png");
        Touch("render_001.png");

        var pages = GhostscriptPageRenderer.ProducedPages(Prefix, firstPage: 5);

        var page = Assert.Single(pages);
        Assert.Equal(5, page.Page);
        Assert.EndsWith("render_001.png", page.File);
    }

    [Fact]
    public void NoOutput_YieldsEmptyMapping()
    {
        Assert.Empty(GhostscriptPageRenderer.ProducedPages(Prefix, firstPage: 1));
    }
}
