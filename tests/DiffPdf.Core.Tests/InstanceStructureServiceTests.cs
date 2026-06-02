using DiffPdf.Core.Network;
using DiffPdf.Pdf.Network;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DiffPdf.Core.Tests;

public class InstanceStructureServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "diffpdf-struct-" + Guid.NewGuid().ToString("N"));

    private static InstanceStructureService Service(NetworkOptions? options = null)
    {
        var opts = Options.Create(options ?? new NetworkOptions());
        var resolver = new NetworkShareResolver(opts);
        var connector = new PlatformShareConnector(opts, NullLogger<PlatformShareConnector>.Instance);
        return new InstanceStructureService(resolver, connector, NullLogger<InstanceStructureService>.Instance);
    }

    private string Base(string name) => Path.Combine(_root, name);

    [Fact]
    public async Task Inspect_EmptyBase_AllMissing()
    {
        string b = Base("empty");
        Directory.CreateDirectory(b);

        var report = await Service().InspectAsync(b, null);

        Assert.True(report.Reachable);
        Assert.False(report.Ok);
        Assert.Equal(new[] { "old", "new", "reports" }, report.Items.Select(i => i.Name).ToArray());
        Assert.All(report.Items, i => Assert.Equal(StructureItemState.Missing, i.State));
    }

    [Fact]
    public async Task Ensure_CreatesAll_AndIsIdempotent()
    {
        string b = Base("provision");

        var first = await Service().EnsureAsync(b, null);
        Assert.True(first.Ok);
        Assert.All(first.Items, i => Assert.Equal(StructureItemState.Created, i.State));
        foreach (var sub in InstanceStructureService.RequiredSubfolders)
            Assert.True(Directory.Exists(Path.Combine(b, sub)));

        var second = await Service().EnsureAsync(b, null);
        Assert.True(second.Ok);
        Assert.All(second.Items, i => Assert.Equal(StructureItemState.Present, i.State));
    }

    [Fact]
    public async Task Ensure_CreatesBasePath_WhenMissing()
    {
        string b = Base("deep/missing/base"); // base itself does not exist yet

        var report = await Service().EnsureAsync(b, null);

        Assert.True(report.Ok);
        Assert.True(Directory.Exists(Path.Combine(b, "old")));
    }

    [Fact]
    public async Task Ensure_RepairsWrongType_FileWhereFolderExpected()
    {
        string b = Base("wrong");
        Directory.CreateDirectory(b);
        File.WriteAllText(Path.Combine(b, "new"), "I am a file, not a folder");

        var inspect = await Service().InspectAsync(b, null);
        Assert.Equal(StructureItemState.WrongType, inspect.Items.Single(i => i.Name == "new").State);

        var ensure = await Service().EnsureAsync(b, null);

        Assert.True(ensure.Ok);
        Assert.Equal(StructureItemState.Repaired, ensure.Items.Single(i => i.Name == "new").State);
        Assert.True(Directory.Exists(Path.Combine(b, "new")));
        Assert.False(File.Exists(Path.Combine(b, "new")));
    }

    [Fact]
    public async Task Inspect_UnknownShare_ReportsError()
    {
        var report = await Service().InspectAsync("share:ghost", null);

        Assert.False(report.Reachable);
        Assert.Contains("Unknown share", report.Error);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* best effort */ }
    }
}
