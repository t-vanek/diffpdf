using DiffPdf.Core.Comparison;

namespace DiffPdf.Core.Tests;

public class PdfFolderScannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "diffpdf-scan-" + Guid.NewGuid().ToString("N"));

    private string Make(params string[] rel)
    {
        foreach (var r in rel)
        {
            var full = Path.Combine(_root, r);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, "%PDF-1.4");
        }
        Directory.CreateDirectory(_root);
        return _root;
    }

    [Fact]
    public void Scan_CountsAll_FiltersPattern_AndCapsSample()
    {
        var dir = Make("a.pdf", "sub/b.pdf", "c.pdf", "notes.txt");

        var all = PdfFolderScanner.Scan(dir, "*.pdf", recursive: true, sampleLimit: null);
        Assert.Equal(3, all.Count);            // *.txt excluded
        Assert.Equal(3, all.Files.Count);
        Assert.Contains("sub/b.pdf", all.Files);

        var capped = PdfFolderScanner.Scan(dir, "*.pdf", recursive: true, sampleLimit: 2);
        Assert.Equal(3, capped.Count);         // count is always complete
        Assert.Equal(2, capped.Files.Count);   // list is capped

        var none = PdfFolderScanner.Scan(dir, "*.pdf", recursive: true, sampleLimit: 0);
        Assert.Equal(3, none.Count);
        Assert.Empty(none.Files);
    }

    [Fact]
    public void Scan_NonRecursive_IgnoresSubfolders()
    {
        var dir = Make("a.pdf", "sub/b.pdf");
        Assert.Equal(1, PdfFolderScanner.Scan(dir, "*.pdf", recursive: false, sampleLimit: null).Count);
    }

    [Fact]
    public void Scan_MissingFolder_Throws()
    {
        Assert.Throws<DirectoryNotFoundException>(() =>
            PdfFolderScanner.Scan(Path.Combine(_root, "nope"), "*.pdf", recursive: true, sampleLimit: null));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* best effort */ }
    }
}
