using System.Text;
using DiffPdf.Application.Abstractions;
using DiffPdf.Application.Files;
using Microsoft.Extensions.Options;

namespace DiffPdf.Core.Tests;

/// <summary>
/// <see cref="FileManagerService"/> over a real temp directory: listing (PDF-only + hidden filtering,
/// ordering), upload (atomic temp file, magic bytes, size cap, conflicts), folder create, delete
/// (recursive semantics), rename (.pdf rule, case-only), download resolution and path safety.
/// </summary>
public sealed class FileManagerServiceTests : IDisposable
{
    private static readonly byte[] MinimalPdf = Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj\nendobj\n%%EOF");

    private readonly string _root;
    private readonly FileManagerService _service;

    public FileManagerServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "diffpdf-tests", "fm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _service = CreateService(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private static FileManagerService CreateService(
        string? rootPath, string? scopeSyncRoot = null, int maxUploadMb = 256, bool validateMagic = true) => new(
        Options.Create(new FileManagerOptions
        {
            RootPath = rootPath,
            MaxUploadSizeMB = maxUploadMb,
            ValidatePdfMagicBytes = validateMagic,
        }),
        Options.Create(new ScopeSyncOptions { RootPath = scopeSyncRoot }));

    private string Abs(string virtualPath) => Path.Combine(_root, virtualPath.Replace('/', Path.DirectorySeparatorChar));

    private void WriteFile(string virtualPath, byte[]? content = null)
    {
        string abs = Abs(virtualPath);
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        File.WriteAllBytes(abs, content ?? MinimalPdf);
    }

    private Task<FileUploadResult> UploadAsync(string dir, string name, byte[] content, bool overwrite = false) =>
        _service.UploadAsync(dir, name, new MemoryStream(content), overwrite);

    // ---------------- root configuration ----------------

    [Fact]
    public void UnconfiguredRoot_ReportsRootNotConfigured()
    {
        var service = CreateService(rootPath: null, scopeSyncRoot: null);
        Assert.Equal(FileOpStatus.RootNotConfigured, service.List("").Status);
        Assert.Equal(FileOpStatus.RootNotConfigured, service.Delete("x", recursive: false).Status);
    }

    [Fact]
    public void FallsBackToScopeSyncRoot()
    {
        var service = CreateService(rootPath: "", scopeSyncRoot: _root);
        WriteFile("fallback.pdf");
        var list = service.List("");
        Assert.Equal(FileOpStatus.Ok, list.Status);
        Assert.Contains(list.Items!, i => i.Name == "fallback.pdf");
    }

    [Fact]
    public void MissingRootFolder_IsCreatedOnDemand()
    {
        string root = Path.Combine(_root, "lazy-root");
        var service = CreateService(root);
        Assert.Equal(FileOpStatus.Ok, service.List("").Status);
        Assert.True(Directory.Exists(root));
    }

    // ---------------- listing ----------------

    [Fact]
    public void List_ShowsFoldersFirst_HidesNonPdfAndHidden()
    {
        Directory.CreateDirectory(Abs("zfolder"));
        WriteFile("beta.pdf");
        WriteFile("Alfa.PDF");      // extension match is case-insensitive
        WriteFile("readme.txt");    // non-PDF files are invisible
        WriteFile("no-extension");
        var hidden = new DirectoryInfo(Abs("hidden-dir"));
        hidden.Create();
        hidden.Attributes |= FileAttributes.Hidden;

        var list = _service.List(null);

        Assert.Equal(FileOpStatus.Ok, list.Status);
        Assert.Equal("", list.CurrentPath);
        Assert.Null(list.ParentPath);
        Assert.Equal(new[] { "zfolder", "Alfa.PDF", "beta.pdf" }, list.Items!.Select(i => i.Name));
        Assert.True(list.Items![0].IsDirectory);
        Assert.Equal((long)MinimalPdf.Length, list.Items![1].SizeBytes);
    }

    [Fact]
    public void List_NestedFolder_ReportsParentPath()
    {
        WriteFile("a/b/doc.pdf");
        var list = _service.List("a/b");
        Assert.Equal(FileOpStatus.Ok, list.Status);
        Assert.Equal("a/b", list.CurrentPath);
        Assert.Equal("a", list.ParentPath);
        Assert.Equal("a/b/doc.pdf", Assert.Single(list.Items!).Path);
    }

    [Theory]
    [InlineData("missing", FileOpStatus.NotFound)]
    [InlineData("../escape", FileOpStatus.InvalidPath)]
    [InlineData(@"C:\windows", FileOpStatus.InvalidPath)]
    public void List_ErrorStates(string path, FileOpStatus expected) =>
        Assert.Equal(expected, _service.List(path).Status);

    [Fact]
    public void List_FilePath_ReportsNotAFolder()
    {
        WriteFile("doc.pdf");
        Assert.Equal(FileOpStatus.NotAFolder, _service.List("doc.pdf").Status);
    }

    // ---------------- upload ----------------

    [Fact]
    public async Task Upload_WritesFile_AndReturnsEntry()
    {
        Directory.CreateDirectory(Abs("in"));
        var result = await UploadAsync("in", "smlouva.pdf", MinimalPdf);

        Assert.True(result.Uploaded);
        Assert.Equal("in/smlouva.pdf", result.Entry!.Path);
        Assert.Equal(MinimalPdf, File.ReadAllBytes(Abs("in/smlouva.pdf")));
        Assert.Empty(Directory.GetFiles(Abs("in"), "~upload-*.tmp")); // temp cleaned up
    }

    [Fact]
    public async Task Upload_StripsClientSidePath()
    {
        var result = await UploadAsync("", @"C:\Users\someone\Desktop\faktura.pdf", MinimalPdf);
        Assert.True(result.Uploaded);
        Assert.Equal("faktura.pdf", result.Entry!.Name);
    }

    [Fact]
    public async Task Upload_RejectsNonPdfExtension()
    {
        var result = await UploadAsync("", "notes.txt", MinimalPdf);
        Assert.False(result.Uploaded);
        Assert.Equal(FileUploadErrorCodes.NotPdf, result.ErrorCode);
    }

    [Fact]
    public async Task Upload_RejectsSpoofedContent_WhenMagicValidationOn()
    {
        var result = await UploadAsync("", "fake.pdf", Encoding.ASCII.GetBytes("MZ this is not a pdf"));
        Assert.False(result.Uploaded);
        Assert.Equal(FileUploadErrorCodes.NotPdf, result.ErrorCode);
        Assert.False(File.Exists(Abs("fake.pdf")));
        Assert.Empty(Directory.GetFiles(_root, "~upload-*.tmp"));
    }

    [Fact]
    public async Task Upload_AcceptsAnyContent_WhenMagicValidationOff()
    {
        var service = CreateService(_root, validateMagic: false);
        var result = await service.UploadAsync("", "raw.pdf", new MemoryStream(Encoding.ASCII.GetBytes("anything")), overwrite: false);
        Assert.True(result.Uploaded);
    }

    [Fact]
    public async Task Upload_EnforcesSizeCap()
    {
        var service = CreateService(_root, maxUploadMb: 1);
        byte[] big = new byte[2 * 1024 * 1024];
        MinimalPdf.CopyTo(big, 0);

        var result = await service.UploadAsync("", "big.pdf", new MemoryStream(big), overwrite: false);

        Assert.False(result.Uploaded);
        Assert.Equal(FileUploadErrorCodes.TooLarge, result.ErrorCode);
        Assert.Empty(Directory.GetFiles(_root, "~upload-*.tmp"));
    }

    [Fact]
    public async Task Upload_ConflictWithoutOverwrite_ThenOverwriteReplaces()
    {
        WriteFile("doc.pdf", MinimalPdf);

        var conflict = await UploadAsync("", "doc.pdf", MinimalPdf);
        Assert.False(conflict.Uploaded);
        Assert.Equal(FileUploadErrorCodes.Exists, conflict.ErrorCode);

        byte[] newContent = Encoding.ASCII.GetBytes("%PDF-1.7\nnew\n%%EOF");
        var replaced = await UploadAsync("", "doc.pdf", newContent, overwrite: true);
        Assert.True(replaced.Uploaded);
        Assert.Equal(newContent, File.ReadAllBytes(Abs("doc.pdf")));
    }

    [Fact]
    public async Task Upload_RejectsInvalidName()
    {
        var result = await UploadAsync("", "CON.pdf", MinimalPdf);
        Assert.False(result.Uploaded);
        Assert.Equal(FileUploadErrorCodes.InvalidName, result.ErrorCode);
    }

    [Fact]
    public async Task Upload_RejectsEmptyContent()
    {
        var result = await UploadAsync("", "empty.pdf", Array.Empty<byte>());
        Assert.False(result.Uploaded);
        Assert.Equal(FileUploadErrorCodes.NotPdf, result.ErrorCode);
    }

    // ---------------- folder create ----------------

    [Fact]
    public void CreateFolder_HappyPath_ThenConflict()
    {
        var created = _service.CreateFolder("", "Faktury 2026");
        Assert.Equal(FileOpStatus.Ok, created.Status);
        Assert.Equal("Faktury 2026", created.Entry!.Path);
        Assert.True(created.Entry.IsDirectory);
        Assert.True(Directory.Exists(Abs("Faktury 2026")));

        Assert.Equal(FileOpStatus.Conflict, _service.CreateFolder("", "Faktury 2026").Status);
    }

    [Theory]
    [InlineData("..", FileOpStatus.InvalidName)]
    [InlineData("a/b", FileOpStatus.InvalidName)]
    [InlineData("name.", FileOpStatus.InvalidName)]
    [InlineData("", FileOpStatus.InvalidName)]
    public void CreateFolder_RejectsInvalidNames(string name, FileOpStatus expected) =>
        Assert.Equal(expected, _service.CreateFolder("", name).Status);

    [Fact]
    public void CreateFolder_MissingParent_ReportsNotFound() =>
        Assert.Equal(FileOpStatus.NotFound, _service.CreateFolder("missing", "x").Status);

    [Fact]
    public void CreateFolder_ConflictsWithExistingFile()
    {
        WriteFile("doc.pdf");
        Assert.Equal(FileOpStatus.Conflict, _service.CreateFolder("", "doc.pdf").Status);
    }

    // ---------------- delete ----------------

    [Fact]
    public void Delete_File()
    {
        WriteFile("doc.pdf");
        Assert.Equal(FileOpStatus.Ok, _service.Delete("doc.pdf", recursive: false).Status);
        Assert.False(File.Exists(Abs("doc.pdf")));
    }

    [Fact]
    public void Delete_EmptyFolder_WithoutRecursive()
    {
        Directory.CreateDirectory(Abs("empty"));
        Assert.Equal(FileOpStatus.Ok, _service.Delete("empty", recursive: false).Status);
    }

    [Fact]
    public void Delete_NonEmptyFolder_RequiresRecursive_AndSeesHiddenContent()
    {
        // The PDF-only listing hides the .txt, but delete must still refuse — the folder is not empty.
        WriteFile("full/invisible.txt");

        var blocked = _service.Delete("full", recursive: false);
        Assert.Equal(FileOpStatus.Conflict, blocked.Status);
        Assert.Equal(1, blocked.ItemCount);

        Assert.Equal(FileOpStatus.Ok, _service.Delete("full", recursive: true).Status);
        Assert.False(Directory.Exists(Abs("full")));
    }

    [Fact]
    public void Delete_Root_IsForbidden() =>
        Assert.Equal(FileOpStatus.InvalidPath, _service.Delete("", recursive: true).Status);

    [Fact]
    public void Delete_Missing_ReportsNotFound() =>
        Assert.Equal(FileOpStatus.NotFound, _service.Delete("missing.pdf", recursive: false).Status);

    // ---------------- rename ----------------

    [Fact]
    public void Rename_File()
    {
        WriteFile("old.pdf");
        var result = _service.Rename("old.pdf", "new.pdf");
        Assert.Equal(FileOpStatus.Ok, result.Status);
        Assert.Equal("new.pdf", result.Entry!.Path);
        Assert.False(File.Exists(Abs("old.pdf")));
        Assert.True(File.Exists(Abs("new.pdf")));
    }

    [Fact]
    public void Rename_File_MustKeepPdfExtension()
    {
        WriteFile("doc.pdf");
        var result = _service.Rename("doc.pdf", "doc.txt");
        Assert.Equal(FileOpStatus.InvalidName, result.Status);
        Assert.Contains(".pdf", result.Detail);
    }

    [Fact]
    public void Rename_Folder_HasNoExtensionRule()
    {
        Directory.CreateDirectory(Abs("dir"));
        Assert.Equal(FileOpStatus.Ok, _service.Rename("dir", "renamed dir").Status);
        Assert.True(Directory.Exists(Abs("renamed dir")));
    }

    [Fact]
    public void Rename_Conflict()
    {
        WriteFile("a.pdf");
        WriteFile("b.pdf");
        Assert.Equal(FileOpStatus.Conflict, _service.Rename("a.pdf", "b.pdf").Status);
    }

    [Fact]
    public void Rename_CaseOnly_IsAllowed()
    {
        WriteFile("doc.pdf");
        var result = _service.Rename("doc.pdf", "DOC.pdf");
        Assert.Equal(FileOpStatus.Ok, result.Status);
        Assert.Equal("DOC.pdf", result.Entry!.Name);
    }

    [Fact]
    public void Rename_SameName_IsNoOp()
    {
        WriteFile("doc.pdf");
        var result = _service.Rename("doc.pdf", "doc.pdf");
        Assert.Equal(FileOpStatus.Ok, result.Status);
        Assert.Equal("doc.pdf", result.Entry!.Path);
    }

    [Fact]
    public void Rename_NestedFile_KeepsParentInPath()
    {
        WriteFile("a/b/old.pdf");
        var result = _service.Rename("a/b/old.pdf", "new.pdf");
        Assert.Equal(FileOpStatus.Ok, result.Status);
        Assert.Equal("a/b/new.pdf", result.Entry!.Path);
    }

    [Fact]
    public void Rename_Root_IsForbidden() =>
        Assert.Equal(FileOpStatus.InvalidPath, _service.Rename("", "x").Status);

    // ---------------- move / copy ----------------

    [Fact]
    public void Move_File_IntoFolder()
    {
        WriteFile("doc.pdf");
        Directory.CreateDirectory(Abs("archiv"));

        var result = _service.Move("doc.pdf", "archiv", overwrite: false);

        Assert.Equal(FileOpStatus.Ok, result.Status);
        Assert.Equal("archiv/doc.pdf", result.Entry!.Path);
        Assert.False(File.Exists(Abs("doc.pdf")));
        Assert.True(File.Exists(Abs("archiv/doc.pdf")));
    }

    [Fact]
    public void Move_Folder_TakesAllContentIncludingHidden()
    {
        WriteFile("zdroj/a.pdf");
        WriteFile("zdroj/poznamky.txt"); // invisible in listings, but a move relocates everything
        Directory.CreateDirectory(Abs("cil"));

        var result = _service.Move("zdroj", "cil", overwrite: false);

        Assert.Equal(FileOpStatus.Ok, result.Status);
        Assert.True(result.Entry!.IsDirectory);
        Assert.True(File.Exists(Abs("cil/zdroj/a.pdf")));
        Assert.True(File.Exists(Abs("cil/zdroj/poznamky.txt")));
        Assert.False(Directory.Exists(Abs("zdroj")));
    }

    [Fact]
    public void Move_FileConflict_RequiresOverwrite()
    {
        WriteFile("doc.pdf", MinimalPdf);
        WriteFile("archiv/doc.pdf");

        Assert.Equal(FileOpStatus.Conflict, _service.Move("doc.pdf", "archiv", overwrite: false).Status);

        byte[] newer = Encoding.ASCII.GetBytes("%PDF-1.7 newer");
        WriteFile("doc.pdf", newer);
        Assert.Equal(FileOpStatus.Ok, _service.Move("doc.pdf", "archiv", overwrite: true).Status);
        Assert.Equal(newer, File.ReadAllBytes(Abs("archiv/doc.pdf")));
    }

    [Fact]
    public void Move_FolderConflict_IsNeverMerged()
    {
        WriteFile("zdroj/a.pdf");
        WriteFile("cil/zdroj/b.pdf"); // target already has a folder of the same name

        Assert.Equal(FileOpStatus.Conflict, _service.Move("zdroj", "cil", overwrite: true).Status);
        Assert.True(Directory.Exists(Abs("zdroj"))); // nothing happened
    }

    [Fact]
    public void Move_IntoOwnSubtree_IsRejected()
    {
        WriteFile("zdroj/sub/a.pdf");
        var intoSelf = _service.Move("zdroj", "zdroj/sub", overwrite: false);
        Assert.Equal(FileOpStatus.InvalidPath, intoSelf.Status);
        Assert.Contains("inside", intoSelf.Detail);

        Assert.Equal(FileOpStatus.InvalidPath, _service.Move("zdroj", "zdroj", overwrite: false).Status);
    }

    [Fact]
    public void Move_SameLocation_ReportsConflict()
    {
        WriteFile("a/doc.pdf");
        var result = _service.Move("a/doc.pdf", "a", overwrite: true); // overwrite must not let it eat itself
        Assert.Equal(FileOpStatus.Conflict, result.Status);
        Assert.True(File.Exists(Abs("a/doc.pdf")));
    }

    [Fact]
    public void Move_RootOrMissing_Rejected()
    {
        Assert.Equal(FileOpStatus.InvalidPath, _service.Move("", "a", overwrite: false).Status);
        Assert.Equal(FileOpStatus.NotFound, _service.Move("missing.pdf", "", overwrite: false).Status);
        WriteFile("doc.pdf");
        Assert.Equal(FileOpStatus.NotFound, _service.Move("doc.pdf", "missing-dir", overwrite: false).Status);
    }

    [Fact]
    public void Copy_File_LeavesSourceInPlace()
    {
        WriteFile("doc.pdf");
        Directory.CreateDirectory(Abs("kopie"));

        var result = _service.Copy("doc.pdf", "kopie", overwrite: false);

        Assert.Equal(FileOpStatus.Ok, result.Status);
        Assert.True(File.Exists(Abs("doc.pdf")));
        Assert.True(File.Exists(Abs("kopie/doc.pdf")));
    }

    [Fact]
    public void Copy_Folder_ReplicatesOnlyFoldersAndPdfs()
    {
        WriteFile("zdroj/a.pdf");
        WriteFile("zdroj/sub/b.pdf");
        WriteFile("zdroj/poznamky.txt"); // invisible → stays behind on copy
        Directory.CreateDirectory(Abs("zdroj/prazdna"));
        Directory.CreateDirectory(Abs("cil"));

        var result = _service.Copy("zdroj", "cil", overwrite: false);

        Assert.Equal(FileOpStatus.Ok, result.Status);
        Assert.True(File.Exists(Abs("cil/zdroj/a.pdf")));
        Assert.True(File.Exists(Abs("cil/zdroj/sub/b.pdf")));
        Assert.True(Directory.Exists(Abs("cil/zdroj/prazdna")));
        Assert.False(File.Exists(Abs("cil/zdroj/poznamky.txt")));
        Assert.True(File.Exists(Abs("zdroj/a.pdf"))); // source intact
    }

    [Fact]
    public void Copy_IntoOwnSubtree_IsRejected()
    {
        WriteFile("zdroj/sub/a.pdf");
        Assert.Equal(FileOpStatus.InvalidPath, _service.Copy("zdroj", "zdroj/sub", overwrite: false).Status);
    }

    [Fact]
    public void Copy_FileConflict_HonoursOverwrite()
    {
        WriteFile("doc.pdf", MinimalPdf);
        WriteFile("kopie/doc.pdf");

        Assert.Equal(FileOpStatus.Conflict, _service.Copy("doc.pdf", "kopie", overwrite: false).Status);
        Assert.Equal(FileOpStatus.Ok, _service.Copy("doc.pdf", "kopie", overwrite: true).Status);
        Assert.Equal(MinimalPdf, File.ReadAllBytes(Abs("kopie/doc.pdf")));
    }

    // ---------------- search ----------------

    [Fact]
    public void Search_FindsByNameCaseInsensitive_RecursivelyAndSorted()
    {
        WriteFile("Faktura-2026.pdf");
        WriteFile("archiv/faktura-2025.pdf");
        WriteFile("archiv/smlouva.pdf");
        WriteFile("archiv/faktura.txt"); // non-PDF never matches

        var result = _service.Search("", "FAKTURA", recursive: true);

        Assert.Equal(FileOpStatus.Ok, result.Status);
        Assert.Equal(new[] { "archiv/faktura-2025.pdf", "Faktura-2026.pdf" },
            result.Items!.Select(i => i.Path)); // sorted by path
        Assert.False(result.Truncated);
    }

    [Fact]
    public void Search_NonRecursive_StaysInFolder()
    {
        WriteFile("a.pdf");
        WriteFile("sub/a-deep.pdf");
        var result = _service.Search("", "a", recursive: false);
        Assert.Equal(new[] { "a.pdf" }, result.Items!.Select(i => i.Path));
    }

    [Fact]
    public void Search_CapsResults_AndFlagsTruncation()
    {
        for (int i = 0; i < 5; i++) WriteFile($"match-{i}.pdf");

        var capped = CreateServiceWithSearchCap(_root, maxResults: 3).Search("", "match", recursive: true);
        Assert.Equal(3, capped.Items!.Count);
        Assert.True(capped.Truncated);
    }

    [Fact]
    public void Search_EmptyQuery_IsRejected()
    {
        Assert.Equal(FileOpStatus.InvalidName, _service.Search("", "  ", recursive: true).Status);
        Assert.Equal(FileOpStatus.InvalidName, _service.Search("", null, recursive: true).Status);
    }

    [Fact]
    public void Search_FromSubfolder_ReturnsPathsRelativeToRoot()
    {
        WriteFile("archiv/2026/faktura.pdf");
        var result = _service.Search("archiv", "faktura", recursive: true);
        Assert.Equal("archiv", result.SearchPath);
        Assert.Equal("archiv/2026/faktura.pdf", Assert.Single(result.Items!).Path);
    }

    private static FileManagerService CreateServiceWithSearchCap(string root, int maxResults) => new(
        Options.Create(new FileManagerOptions { RootPath = root, MaxSearchResults = maxResults }),
        Options.Create(new ScopeSyncOptions()));

    // ---------------- download ----------------

    [Fact]
    public void Download_ResolvesAbsolutePath()
    {
        WriteFile("a/doc.pdf");
        var result = _service.ResolveDownload("a/doc.pdf");
        Assert.Equal(FileOpStatus.Ok, result.Status);
        Assert.Equal(Abs("a/doc.pdf"), result.AbsolutePath);
        Assert.Equal("doc.pdf", result.FileName);
    }

    [Fact]
    public void Download_Folder_ReportsNotAFile()
    {
        Directory.CreateDirectory(Abs("dir"));
        Assert.Equal(FileOpStatus.NotAFile, _service.ResolveDownload("dir").Status);
    }

    [Fact]
    public void Download_Traversal_ReportsInvalidPath() =>
        Assert.Equal(FileOpStatus.InvalidPath, _service.ResolveDownload("../outside.pdf").Status);

    // ---------------- status diagnostics ----------------

    [Fact]
    public void GetStatus_Unconfigured_ReportsState()
    {
        var status = CreateService(rootPath: null, scopeSyncRoot: null).GetStatus();
        Assert.False(status.Configured);
        Assert.Null(status.ResolvedFrom);
        Assert.Null(status.RootPath);
        Assert.False(status.RootExists);
        Assert.False(status.Writable);
        Assert.Equal(256, status.MaxUploadSizeMB); // limits are reported even without a root
    }

    [Fact]
    public void GetStatus_ConfiguredRoot_ReportsWritableAndSpace()
    {
        var status = _service.GetStatus();
        Assert.True(status.Configured);
        Assert.Equal("FileManager:RootPath", status.ResolvedFrom);
        Assert.Equal(Path.GetFullPath(_root), status.RootPath);
        Assert.True(status.RootExists);
        Assert.True(status.Writable);
        Assert.Null(status.Error);
        Assert.NotNull(status.FreeSpaceBytes); // local volume exposes free space
        Assert.NotNull(status.TotalSpaceBytes);
        Assert.Empty(Directory.GetFiles(_root, "~probe-*.tmp")); // probe cleaned up
    }

    [Fact]
    public void GetStatus_FallbackRoot_ReportsScopeSyncSource()
    {
        var status = CreateService(rootPath: "", scopeSyncRoot: _root).GetStatus();
        Assert.Equal("ScopeSync:RootPath", status.ResolvedFrom);
        Assert.True(status.Writable);
    }

    [Fact]
    public void GetStatus_MissingRootFolder_IsCreatedLikeFirstUse()
    {
        string root = Path.Combine(_root, "lazy-status-root");
        var status = CreateService(root).GetStatus();
        Assert.True(status.RootExists); // the probe mirrors Resolve(): auto-created on demand
        Assert.True(Directory.Exists(root));
    }

    // ---------------- upload directory validation ----------------

    [Fact]
    public void ValidateDirectory_RootAndNested()
    {
        Directory.CreateDirectory(Abs("in"));
        Assert.Equal(FileOpStatus.Ok, _service.ValidateDirectory("").Status);
        Assert.Equal(FileOpStatus.Ok, _service.ValidateDirectory("in").Status);
        Assert.Equal(FileOpStatus.NotFound, _service.ValidateDirectory("missing").Status);
    }

    [Fact]
    public void ValidateDirectory_File_ReportsNotAFolder()
    {
        WriteFile("doc.pdf");
        Assert.Equal(FileOpStatus.NotAFolder, _service.ValidateDirectory("doc.pdf").Status);
    }
}
