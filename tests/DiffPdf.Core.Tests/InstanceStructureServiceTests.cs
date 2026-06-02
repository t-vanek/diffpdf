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

    [Fact]
    public async Task Ensure_ReportsEmptyInputFolders_NoFileList()
    {
        string b = Base("content-empty");

        var report = await Service().EnsureAsync(b, null);

        Assert.Equal(0, report.Items.Single(i => i.Name == "old").PdfCount);
        Assert.Equal(0, report.Items.Single(i => i.Name == "new").PdfCount);
        Assert.Null(report.Items.Single(i => i.Name == "old").Files);    // includeFiles default false
        Assert.Null(report.Items.Single(i => i.Name == "reports").PdfCount); // not an input folder
    }

    [Fact]
    public async Task Inspect_CountsPdfsRecursively_AndListsWhenRequested()
    {
        string b = Base("content-full");
        Directory.CreateDirectory(Path.Combine(b, "old", "sub"));
        Directory.CreateDirectory(Path.Combine(b, "new"));
        File.WriteAllText(Path.Combine(b, "old", "a.pdf"), "%PDF-1.4");
        File.WriteAllText(Path.Combine(b, "old", "sub", "bb.pdf"), "%PDF-1.4");
        File.WriteAllText(Path.Combine(b, "old", "notes.txt"), "x");

        var report = await Service().InspectAsync(b, null, includeFiles: true);

        var old = report.Items.Single(i => i.Name == "old");
        Assert.Equal(2, old.PdfCount); // only *.pdf, recursive
        Assert.NotNull(old.Files);
        Assert.Contains("a.pdf", old.Files!);
        Assert.Contains("sub/bb.pdf", old.Files!);
        Assert.DoesNotContain(old.Files!, f => f.EndsWith(".txt"));

        var @new = report.Items.Single(i => i.Name == "new");
        Assert.Equal(0, @new.PdfCount);
        Assert.NotNull(@new.Files);  // includeFiles true -> empty list, not null
        Assert.Empty(@new.Files!);

        Assert.Null(report.Items.Single(i => i.Name == "reports").PdfCount);
    }

    [Fact]
    public async Task Inspect_WithoutIncludeFiles_ReportsCount_ButOmitsList()
    {
        string b = Base("content-countonly");
        Directory.CreateDirectory(Path.Combine(b, "old"));
        File.WriteAllText(Path.Combine(b, "old", "a.pdf"), "%PDF-1.4");

        var report = await Service().InspectAsync(b, null); // includeFiles default false

        var old = report.Items.Single(i => i.Name == "old");
        Assert.Equal(1, old.PdfCount);
        Assert.Null(old.Files);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* best effort */ }
    }
}
