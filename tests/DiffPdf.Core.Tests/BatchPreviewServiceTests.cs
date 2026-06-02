using DiffPdf.Core.Network;
using DiffPdf.Core.Preview;
using DiffPdf.Pdf.Network;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DiffPdf.Core.Tests;

public class BatchPreviewServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "diffpdf-prev-" + Guid.NewGuid().ToString("N"));

    private BatchPreviewService Service(NetworkOptions? options = null)
    {
        var opts = Options.Create(options ?? new NetworkOptions());
        var resolver = new NetworkShareResolver(opts);
        var connector = new PlatformShareConnector(opts, NullLogger<PlatformShareConnector>.Instance);
        return new BatchPreviewService(resolver, connector, NullLogger<BatchPreviewService>.Instance);
    }

    private string MakeFolder(string name, params string[] relativeFiles)
    {
        string dir = Path.Combine(_root, name);
        foreach (var rel in relativeFiles)
        {
            string full = Path.Combine(dir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, "%PDF-1.4");
        }
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public async Task InspectFolder_CountsPdfs_AndSamples()
    {
        string dir = MakeFolder("old", "a.pdf", "sub/b.pdf", "notes.txt");

        var result = await Service().InspectFolderAsync(dir);

        Assert.True(result.Reachable);
        Assert.Null(result.Error);
        Assert.Equal(2, result.FileCount); // only *.pdf
        Assert.Contains("a.pdf", result.SampleFiles);
        Assert.Contains("sub/b.pdf", result.SampleFiles);
    }

    [Fact]
    public async Task InspectFolder_MissingFolder_ReportsUnreachable()
    {
        var result = await Service().InspectFolderAsync(Path.Combine(_root, "does-not-exist"));

        Assert.False(result.Reachable);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task InspectFolder_UnknownShare_ReportsConfigError()
    {
        var result = await Service().InspectFolderAsync("share:ghost/x");

        Assert.False(result.Reachable);
        Assert.Contains("Unknown share", result.Error);
    }

    [Fact]
    public async Task PreviewPairing_ClassifiesMatchedAndUnmatched()
    {
        string oldDir = MakeFolder("old", "same.pdf", "only-old.pdf");
        string newDir = MakeFolder("new", "same.pdf", "only-new.pdf");

        var preview = await Service().PreviewPairingAsync(oldDir, newDir);

        Assert.True(preview.Reachable);
        Assert.Equal(3, preview.Total);
        Assert.Equal(1, preview.Matched);
        Assert.Equal(1, preview.OnlyInOld);
        Assert.Equal(1, preview.OnlyInNew);
        Assert.Contains("only-old.pdf", preview.SampleOnlyInOld);
        Assert.Contains("only-new.pdf", preview.SampleOnlyInNew);
    }

    [Fact]
    public async Task PreviewPairing_ResolvesShareAlias_ToLocalMount()
    {
        string oldDir = MakeFolder("old", "x.pdf");
        string newDir = MakeFolder("new", "x.pdf");

        var options = new NetworkOptions
        {
            Shares = { ["base"] = new NetworkShareDefinition { LocalMountPath = oldDir } },
        };

        var preview = await Service(options).PreviewPairingAsync("share:base", newDir);

        Assert.True(preview.Reachable);
        Assert.Equal(1, preview.Matched);
        Assert.Equal(oldDir, preview.OldResolvedPath);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* best effort */ }
    }
}
