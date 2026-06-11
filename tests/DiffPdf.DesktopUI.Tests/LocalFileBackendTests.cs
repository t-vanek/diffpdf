using DiffPdf.Client;
using DiffPdf.DesktopUI.Services;

namespace DiffPdf.DesktopUI.Tests;

/// <summary>
/// <see cref="LocalFileBackend"/> over a real temp directory: the drive-list root, PDF-only listing,
/// CRUD with the shared conflict semantics (folders never merge, non-empty delete needs recursive),
/// move/copy rules mirroring the server backend, search and the streaming write sink.
/// </summary>
public sealed class LocalFileBackendTests : IDisposable
{
    private readonly string _root;
    private readonly LocalFileBackend _backend = new();

    public LocalFileBackendTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "diffpdf-tests", "local-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private string Abs(string relative) => Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));

    private void WriteFile(string relative, string content = "%PDF-1.4 test")
    {
        string abs = Abs(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        File.WriteAllText(abs, content);
    }

    [Fact]
    public async Task EmptyPath_ListsReadyDrives()
    {
        var list = await _backend.ListAsync("");
        Assert.Equal("", list.CurrentPath);
        Assert.Null(list.ParentPath);
        Assert.NotEmpty(list.Items);
        Assert.All(list.Items, i =>
        {
            Assert.Equal(FileItemKind.Folder, i.Kind);
            Assert.EndsWith(@":\", i.Path, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task List_ShowsFoldersAndPdfsOnly_WithRealPaths()
    {
        WriteFile("b.pdf");
        WriteFile("a.PDF");
        WriteFile("poznamky.txt");
        Directory.CreateDirectory(Abs("slozka"));

        var list = await _backend.ListAsync(_root);

        Assert.Equal(_root, list.CurrentPath);
        Assert.Equal(Path.GetDirectoryName(_root), list.ParentPath);
        Assert.Equal(new[] { "slozka", "a.PDF", "b.pdf" }, list.Items.Select(i => i.Name));
        Assert.Equal(Abs("b.pdf"), list.Items[^1].Path);
    }

    [Fact]
    public async Task List_MissingFolder_Throws() =>
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => _backend.ListAsync(Abs("neexistuje")));

    [Fact]
    public void GetParent_WalksUpToDriveListAtTheRoot()
    {
        Assert.Equal(_root, _backend.GetParent(Abs("a.pdf")));
        string driveRoot = Path.GetPathRoot(_root)!;
        Assert.Equal("", _backend.GetParent(driveRoot)); // a drive root's parent is the drive list
        Assert.Equal("", _backend.GetParent(""));
    }

    [Fact]
    public void CanWriteTo_FalseOnlyAtTheDriveList()
    {
        Assert.False(_backend.CanWriteTo(""));
        Assert.True(_backend.CanWriteTo(_root));
    }

    [Fact]
    public async Task CreateFolder_ThenConflict()
    {
        var created = await _backend.CreateFolderAsync(_root, "Faktury 2026");
        Assert.Equal(Abs("Faktury 2026"), created.Path);
        Assert.True(Directory.Exists(created.Path));

        await Assert.ThrowsAsync<FileBackendConflictException>(() => _backend.CreateFolderAsync(_root, "Faktury 2026"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _backend.CreateFolderAsync("", "x"));
    }

    [Fact]
    public async Task Delete_File_And_NonEmptyFolderNeedsRecursive()
    {
        WriteFile("doc.pdf");
        await _backend.DeleteAsync(Abs("doc.pdf"), recursive: false);
        Assert.False(File.Exists(Abs("doc.pdf")));

        WriteFile("plna/skryty.txt"); // invisible in listings, but delete must still refuse
        await Assert.ThrowsAsync<FileBackendConflictException>(() => _backend.DeleteAsync(Abs("plna"), recursive: false));
        await _backend.DeleteAsync(Abs("plna"), recursive: true);
        Assert.False(Directory.Exists(Abs("plna")));

        await Assert.ThrowsAsync<FileNotFoundException>(() => _backend.DeleteAsync(Abs("nic.pdf"), recursive: false));
    }

    [Fact]
    public async Task Rename_File_CaseOnly_AndConflict()
    {
        WriteFile("old.pdf");
        WriteFile("taken.pdf");

        var renamed = await _backend.RenameAsync(Abs("old.pdf"), "new.pdf");
        Assert.Equal(Abs("new.pdf"), renamed.Path);

        await Assert.ThrowsAsync<FileBackendConflictException>(() => _backend.RenameAsync(Abs("new.pdf"), "taken.pdf"));

        var cased = await _backend.RenameAsync(Abs("new.pdf"), "NEW.pdf");
        Assert.Equal("NEW.pdf", cased.Name);
    }

    [Fact]
    public async Task Move_File_IntoFolder_ConflictNeedsOverwrite()
    {
        WriteFile("doc.pdf");
        Directory.CreateDirectory(Abs("archiv"));

        var moved = await _backend.MoveAsync(Abs("doc.pdf"), Abs("archiv"), overwrite: false);
        Assert.Equal(Abs("archiv/doc.pdf"), moved.Path);
        Assert.False(File.Exists(Abs("doc.pdf")));

        WriteFile("doc.pdf", "%PDF-1.7 newer");
        await Assert.ThrowsAsync<FileBackendConflictException>(
            () => _backend.MoveAsync(Abs("doc.pdf"), Abs("archiv"), overwrite: false));
        await _backend.MoveAsync(Abs("doc.pdf"), Abs("archiv"), overwrite: true);
        Assert.Equal("%PDF-1.7 newer", File.ReadAllText(Abs("archiv/doc.pdf")));
    }

    [Fact]
    public async Task Move_Folder_TakesEverything_CopyTakesOnlyPdfs()
    {
        WriteFile("zdroj/a.pdf");
        WriteFile("zdroj/poznamky.txt");
        Directory.CreateDirectory(Abs("cil-move"));
        Directory.CreateDirectory(Abs("cil-copy"));

        await _backend.CopyAsync(Abs("zdroj"), Abs("cil-copy"), overwrite: false);
        Assert.True(File.Exists(Abs("cil-copy/zdroj/a.pdf")));
        Assert.False(File.Exists(Abs("cil-copy/zdroj/poznamky.txt"))); // copy replicates the visible tree

        await _backend.MoveAsync(Abs("zdroj"), Abs("cil-move"), overwrite: false);
        Assert.True(File.Exists(Abs("cil-move/zdroj/poznamky.txt"))); // a move relocates everything
        Assert.False(Directory.Exists(Abs("zdroj")));
    }

    [Fact]
    public async Task Transfer_GuardsCycle_SamePlace_AndDriveLevel()
    {
        WriteFile("zdroj/sub/a.pdf");
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _backend.CopyAsync(Abs("zdroj"), Abs("zdroj/sub"), overwrite: false));

        WriteFile("doc.pdf");
        await Assert.ThrowsAsync<FileBackendConflictException>(
            () => _backend.MoveAsync(Abs("doc.pdf"), _root, overwrite: true)); // same place never eats the file
        Assert.True(File.Exists(Abs("doc.pdf")));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _backend.CopyAsync(Path.GetPathRoot(_root)!, _root, overwrite: false)); // drives can't be copied
    }

    [Fact]
    public async Task Search_Recursive_FindsByName_CaseInsensitive()
    {
        WriteFile("Faktura-2026.pdf");
        WriteFile("archiv/faktura-2025.pdf");
        WriteFile("archiv/smlouva.pdf");
        WriteFile("archiv/faktura.txt");

        var result = await _backend.SearchAsync(_root, "FAKTURA", recursive: true);
        Assert.Equal(2, result.Items.Count);
        Assert.All(result.Items, i => Assert.StartsWith(_root, i.Path, StringComparison.OrdinalIgnoreCase));

        var shallow = await _backend.SearchAsync(_root, "faktura", recursive: false);
        Assert.Equal("Faktura-2026.pdf", Assert.Single(shallow.Items).Name);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _backend.SearchAsync("", "x", recursive: true));
    }

    [Fact]
    public async Task WriteFile_StreamsWithProgress_Conflict_AndOverwrite()
    {
        byte[] payload = System.Text.Encoding.ASCII.GetBytes("%PDF-1.4 payload");
        double lastProgress = 0;
        var progress = new SynchronousProgress(p => lastProgress = p);

        await _backend.WriteFileAsync(_root, "doc.pdf", new MemoryStream(payload), payload.Length, overwrite: false, progress);
        Assert.Equal(payload, File.ReadAllBytes(Abs("doc.pdf")));
        Assert.Equal(1, lastProgress, precision: 3);
        Assert.Empty(Directory.GetFiles(_root, "~transfer-*.tmp")); // temp cleaned up

        await Assert.ThrowsAsync<FileBackendConflictException>(
            () => _backend.WriteFileAsync(_root, "doc.pdf", new MemoryStream(payload), payload.Length, overwrite: false, null));

        byte[] newer = System.Text.Encoding.ASCII.GetBytes("%PDF-1.7 newer");
        await _backend.WriteFileAsync(_root, "doc.pdf", new MemoryStream(newer), newer.Length, overwrite: true, null);
        Assert.Equal(newer, File.ReadAllBytes(Abs("doc.pdf")));
    }

    [Fact]
    public async Task OpenRead_RoundTrips()
    {
        WriteFile("doc.pdf", "obsah");
        await using var stream = await _backend.OpenReadAsync(Abs("doc.pdf"));
        using var reader = new StreamReader(stream);
        Assert.Equal("obsah", await reader.ReadToEndAsync());
    }

    /// <summary>Progress that reports inline (Progress&lt;T&gt; posts to a sync context the test may not pump).</summary>
    private sealed class SynchronousProgress(Action<double> handler) : IProgress<double>
    {
        public void Report(double value) => handler(value);
    }
}
