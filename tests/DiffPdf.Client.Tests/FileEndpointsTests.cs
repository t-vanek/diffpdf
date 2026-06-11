using System.Net;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace DiffPdf.Client.Tests;

/// <summary>
/// Boots the API with the file manager rooted in a fresh temp directory (everything else identical
/// to <see cref="InMemoryApiFactory"/>: in-memory stores, no auth, no scope sync). One factory per
/// test class — the root is created on construction and deleted on dispose.
/// </summary>
public sealed class FileManagerApiFactory : WebApplicationFactory<Program>
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "diffpdf-tests", "files-api-" + Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(Root);
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:SqlServer"] = "",
            ["ScopeSync:RootPath"] = "",
            ["ScopeSync:Enabled"] = "false",
            ["Auth:Enabled"] = "false",
            ["Automation:TickSeconds"] = "3600",
            ["FileManager:RootPath"] = Root,
            ["FileManager:MaxUploadSizeMB"] = "8",
        }));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { /* best effort */ }
    }
}

/// <summary>End-to-end coverage of /api/v1/files through the typed client (list, upload, folder, rename, delete, download).</summary>
public sealed class FileEndpointsTests : IClassFixture<FileManagerApiFactory>
{
    private static readonly byte[] MinimalPdf = Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj\nendobj\n%%EOF");

    private readonly FileManagerApiFactory _factory;
    private readonly DiffPdfClient _client;

    public FileEndpointsTests(FileManagerApiFactory factory)
    {
        _factory = factory;
        _client = new DiffPdfClient(factory.CreateClient());
    }

    /// <summary>Each test works in its own folder — the class fixture shares one root.</summary>
    private async Task<string> CreateWorkFolderAsync(string name)
    {
        var folder = await _client.CreateFolderAsync(new CreateFolderRequest("", name));
        return folder.Path;
    }

    [Fact]
    public async Task List_EmptyRoot_ReturnsRootListing()
    {
        var list = await _client.ListFilesAsync();
        Assert.Equal("", list.CurrentPath);
        Assert.Null(list.ParentPath);
    }

    [Fact]
    public async Task Upload_List_Download_RoundTrip()
    {
        string dir = await CreateWorkFolderAsync("roundtrip");

        double lastProgress = 0;
        var progress = new Progress<double>(p => lastProgress = p);
        var uploaded = await _client.UploadFileAsync(dir, "smlouva.pdf", new MemoryStream(MinimalPdf), progress: progress);

        Assert.True(uploaded.Uploaded);
        Assert.Equal($"{dir}/smlouva.pdf", uploaded.Path);
        Assert.Equal(MinimalPdf.Length, uploaded.SizeBytes);

        var list = await _client.ListFilesAsync(dir);
        Assert.Equal(dir, list.CurrentPath);
        Assert.Equal("", list.ParentPath);
        var item = Assert.Single(list.Items);
        Assert.Equal(FileItemKind.Pdf, item.Kind);
        Assert.Equal("smlouva.pdf", item.Name);
        Assert.Equal(MinimalPdf.Length, item.SizeBytes);

        byte[] downloaded = await _client.DownloadFileAsync(uploaded.Path!);
        Assert.Equal(MinimalPdf, downloaded);

        using var memory = new MemoryStream();
        await _client.DownloadFileAsync(uploaded.Path!, memory);
        Assert.Equal(MinimalPdf, memory.ToArray());
    }

    [Fact]
    public async Task Upload_Conflict_ReportsSoftError_ThenOverwriteSucceeds()
    {
        string dir = await CreateWorkFolderAsync("conflict");
        await _client.UploadFileAsync(dir, "doc.pdf", new MemoryStream(MinimalPdf));

        var conflict = await _client.UploadFileAsync(dir, "doc.pdf", new MemoryStream(MinimalPdf));
        Assert.False(conflict.Uploaded);
        Assert.Equal(FileUploadErrorCodes.Exists, conflict.ErrorCode);

        var replaced = await _client.UploadFileAsync(dir, "doc.pdf", new MemoryStream(MinimalPdf), overwrite: true);
        Assert.True(replaced.Uploaded);
    }

    [Fact]
    public async Task Upload_NonPdfContent_ReportsSoftError()
    {
        string dir = await CreateWorkFolderAsync("spoof");
        var result = await _client.UploadFileAsync(dir, "fake.pdf", new MemoryStream(Encoding.ASCII.GetBytes("not a pdf")));
        Assert.False(result.Uploaded);
        Assert.Equal(FileUploadErrorCodes.NotPdf, result.ErrorCode);
    }

    [Fact]
    public async Task Upload_IntoMissingFolder_Throws404()
    {
        var ex = await Assert.ThrowsAsync<DiffPdfApiException>(
            () => _client.UploadFileAsync("does-not-exist", "x.pdf", new MemoryStream(MinimalPdf)));
        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
    }

    [Fact]
    public async Task CreateFolder_Conflict_Throws409()
    {
        await CreateWorkFolderAsync("dup");
        var ex = await Assert.ThrowsAsync<DiffPdfApiException>(
            () => _client.CreateFolderAsync(new CreateFolderRequest("", "dup")));
        Assert.Equal(HttpStatusCode.Conflict, ex.StatusCode);
    }

    [Fact]
    public async Task Rename_File_And_ConflictCases()
    {
        string dir = await CreateWorkFolderAsync("rename");
        await _client.UploadFileAsync(dir, "old.pdf", new MemoryStream(MinimalPdf));
        await _client.UploadFileAsync(dir, "taken.pdf", new MemoryStream(MinimalPdf));

        var renamed = await _client.RenameFileAsync(new RenameFileRequest($"{dir}/old.pdf", "new.pdf"));
        Assert.Equal($"{dir}/new.pdf", renamed.Path);
        Assert.Equal(FileItemKind.Pdf, renamed.Kind);

        var conflict = await Assert.ThrowsAsync<DiffPdfApiException>(
            () => _client.RenameFileAsync(new RenameFileRequest($"{dir}/new.pdf", "taken.pdf")));
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);

        var badExtension = await Assert.ThrowsAsync<DiffPdfApiException>(
            () => _client.RenameFileAsync(new RenameFileRequest($"{dir}/new.pdf", "new.txt")));
        Assert.Equal(HttpStatusCode.BadRequest, badExtension.StatusCode);
    }

    [Fact]
    public async Task Delete_NonEmptyFolder_Throws409_ThenRecursiveSucceeds()
    {
        string dir = await CreateWorkFolderAsync("to-delete");
        await _client.UploadFileAsync(dir, "doc.pdf", new MemoryStream(MinimalPdf));

        var ex = await Assert.ThrowsAsync<DiffPdfApiException>(() => _client.DeleteFileAsync(dir));
        Assert.Equal(HttpStatusCode.Conflict, ex.StatusCode);

        await _client.DeleteFileAsync(dir, recursive: true);
        var notFound = await Assert.ThrowsAsync<DiffPdfApiException>(() => _client.ListFilesAsync(dir));
        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
    }

    [Fact]
    public async Task Delete_File()
    {
        string dir = await CreateWorkFolderAsync("delete-file");
        var uploaded = await _client.UploadFileAsync(dir, "doc.pdf", new MemoryStream(MinimalPdf));

        await _client.DeleteFileAsync(uploaded.Path!);
        Assert.Empty((await _client.ListFilesAsync(dir)).Items);
    }

    [Fact]
    public async Task Move_File_BetweenFolders_AndConflictNeedsOverwrite()
    {
        string source = await CreateWorkFolderAsync("move-src");
        string target = await CreateWorkFolderAsync("move-dst");
        await _client.UploadFileAsync(source, "doc.pdf", new MemoryStream(MinimalPdf));

        var moved = await _client.MoveFileAsync(new MoveFileRequest($"{source}/doc.pdf", target));
        Assert.Equal($"{target}/doc.pdf", moved.Path);
        Assert.Empty((await _client.ListFilesAsync(source)).Items);

        // A second file of the same name needs Overwrite to replace the moved one.
        await _client.UploadFileAsync(source, "doc.pdf", new MemoryStream(MinimalPdf));
        var conflict = await Assert.ThrowsAsync<DiffPdfApiException>(
            () => _client.MoveFileAsync(new MoveFileRequest($"{source}/doc.pdf", target)));
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);

        await _client.MoveFileAsync(new MoveFileRequest($"{source}/doc.pdf", target, Overwrite: true));
        Assert.Single((await _client.ListFilesAsync(target)).Items);
    }

    [Fact]
    public async Task Copy_Folder_ReplicatesTree_AndCycleIsRejected()
    {
        string source = await CreateWorkFolderAsync("copy-src");
        string target = await CreateWorkFolderAsync("copy-dst");
        await _client.CreateFolderAsync(new CreateFolderRequest(source, "sub"));
        await _client.UploadFileAsync($"{source}/sub", "deep.pdf", new MemoryStream(MinimalPdf));

        var copied = await _client.CopyFileAsync(new CopyFileRequest(source, target));
        Assert.Equal($"{target}/copy-src", copied.Path);
        Assert.Equal(FileItemKind.Folder, copied.Kind);
        var deep = await _client.ListFilesAsync($"{target}/copy-src/sub");
        Assert.Equal("deep.pdf", Assert.Single(deep.Items).Name);
        Assert.Single((await _client.ListFilesAsync($"{source}/sub")).Items); // source intact

        var cycle = await Assert.ThrowsAsync<DiffPdfApiException>(
            () => _client.CopyFileAsync(new CopyFileRequest(source, $"{source}/sub")));
        Assert.Equal(HttpStatusCode.BadRequest, cycle.StatusCode);
    }

    [Fact]
    public async Task Search_FindsRecursively_RespectsScope()
    {
        string dir = await CreateWorkFolderAsync("search-root");
        await _client.CreateFolderAsync(new CreateFolderRequest(dir, "2026"));
        await _client.UploadFileAsync(dir, "faktura-leden.pdf", new MemoryStream(MinimalPdf));
        await _client.UploadFileAsync($"{dir}/2026", "faktura-unor.pdf", new MemoryStream(MinimalPdf));
        await _client.UploadFileAsync($"{dir}/2026", "smlouva.pdf", new MemoryStream(MinimalPdf));

        var result = await _client.SearchFilesAsync(dir, "FAKTURA");
        Assert.Equal(dir, result.SearchPath);
        Assert.Equal(2, result.Items.Count);
        Assert.All(result.Items, i => Assert.StartsWith(dir + "/", i.Path, StringComparison.Ordinal));
        Assert.False(result.Truncated);

        var shallow = await _client.SearchFilesAsync(dir, "faktura", recursive: false);
        Assert.Equal("faktura-leden.pdf", Assert.Single(shallow.Items).Name);

        var empty = await Assert.ThrowsAsync<DiffPdfApiException>(() => _client.SearchFilesAsync(dir, "   "));
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData(@"C:\windows")]
    [InlineData(@"\\server\share")]
    public async Task TraversalPaths_Throw400(string path)
    {
        var ex = await Assert.ThrowsAsync<DiffPdfApiException>(() => _client.ListFilesAsync(path));
        Assert.Equal(HttpStatusCode.BadRequest, ex.StatusCode);

        ex = await Assert.ThrowsAsync<DiffPdfApiException>(() => _client.DownloadFileAsync(path));
        Assert.Equal(HttpStatusCode.BadRequest, ex.StatusCode);
    }

    [Fact]
    public async Task Download_Folder_Throws400()
    {
        string dir = await CreateWorkFolderAsync("downloadable");
        var ex = await Assert.ThrowsAsync<DiffPdfApiException>(() => _client.DownloadFileAsync(dir));
        Assert.Equal(HttpStatusCode.BadRequest, ex.StatusCode);
    }

    [Fact]
    public async Task Delete_Root_Throws400()
    {
        var ex = await Assert.ThrowsAsync<DiffPdfApiException>(() => _client.DeleteFileAsync(""));
        Assert.Equal(HttpStatusCode.BadRequest, ex.StatusCode);
    }

    [Fact]
    public async Task Status_ReportsConfiguredWritableRoot()
    {
        var status = await _client.GetFileManagerStatusAsync();
        Assert.True(status.Configured);
        Assert.Equal("FileManager:RootPath", status.ResolvedFrom);
        Assert.Equal(Path.GetFullPath(_factory.Root), status.RootPath);
        Assert.True(status.RootExists);
        Assert.True(status.Writable);
        Assert.Equal(8, status.MaxUploadSizeMB); // the factory's configured limit round-trips
    }
}

/// <summary>Without FileManager:RootPath (and with ScopeSync:RootPath cleared) the file endpoints answer 503.</summary>
public sealed class FileEndpointsUnconfiguredTests : IClassFixture<InMemoryApiFactory>
{
    private readonly DiffPdfClient _client;

    public FileEndpointsUnconfiguredTests(InMemoryApiFactory factory) =>
        _client = new DiffPdfClient(factory.CreateClient());

    [Fact]
    public async Task List_WithoutConfiguredRoot_Throws503()
    {
        var ex = await Assert.ThrowsAsync<DiffPdfApiException>(() => _client.ListFilesAsync());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ex.StatusCode);
    }

    [Fact]
    public async Task Status_WithoutConfiguredRoot_Is200_WithConfiguredFalse()
    {
        // Unlike the file operations, the diagnostics endpoint treats "not configured" as a state, not an error.
        var status = await _client.GetFileManagerStatusAsync();
        Assert.False(status.Configured);
        Assert.Null(status.ResolvedFrom);
        Assert.Null(status.RootPath);
    }
}
