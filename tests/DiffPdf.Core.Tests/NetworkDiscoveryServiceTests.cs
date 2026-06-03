using DiffPdf.Core.Network;
using DiffPdf.Pdf.Network;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DiffPdf.Core.Tests;

public class NetworkDiscoveryServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "diffpdf-disc-" + Guid.NewGuid().ToString("N"));

    private NetworkDiscoveryService Service(NetworkOptions? options = null)
    {
        var opts = Options.Create(options ?? new NetworkOptions());
        var resolver = new NetworkShareResolver(opts);
        var connector = new PlatformShareConnector(opts, NullLogger<PlatformShareConnector>.Instance);
        return new NetworkDiscoveryService(resolver, connector, NullLogger<NetworkDiscoveryService>.Instance);
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
