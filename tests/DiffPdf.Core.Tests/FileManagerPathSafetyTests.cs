using System.Diagnostics;
using DiffPdf.Application.Abstractions;
using DiffPdf.Application.Files;
using DiffPdf.Core.Abstractions;
using DiffPdf.Core.Models;
using DiffPdf.Core.Network;
using Microsoft.Extensions.Options;

namespace DiffPdf.Core.Tests;

public sealed class FileManagerPathSafetyTests : IDisposable
{
    private readonly string _fixture = Path.Combine(Path.GetTempPath(), "diffpdf-path-safety-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _links = [];
    private string Managed => Path.Combine(_fixture, "managed");
    private string Outside => Path.Combine(_fixture, "outside");
    public FileManagerPathSafetyTests()
    {
        Directory.CreateDirectory(Managed);
        Directory.CreateDirectory(Outside);
        File.WriteAllText(Path.Combine(Outside, "marker.pdf"), "%PDF-synthetic");
    }

    private FileManagerService Service(string? root = null) => new(
        Options.Create(new FileManagerOptions { RootPath = root ?? Managed }), Options.Create(new ScopeSyncOptions()));

    private void Link(string path, string target)
    {
        if (OperatingSystem.IsWindows())
        {
            var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("/c");
            start.ArgumentList.Add("mklink");
            start.ArgumentList.Add("/J");
            start.ArgumentList.Add(path);
            start.ArgumentList.Add(target);
            using var process = Process.Start(start)!;
            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, output + error);
        }
        else Directory.CreateSymbolicLink(path, target);
        _links.Add(path);
    }

    [Fact]
    public async Task LinkCannotBeUsedAsSourceDestinationOrRoot()
    {
        string link = Path.Combine(Managed, "link");
        Link(link, Outside);
        using var files = Service();
        Assert.Equal(FileOpStatus.InvalidPath, files.List("link").Status);
        Assert.Equal(FileOpStatus.InvalidPath, files.ValidateDirectory("link").Status);
        Assert.Equal(FileOpStatus.InvalidPath, files.ResolveDownload("link/marker.pdf").Status);
        Assert.Equal(FileOpStatus.InvalidPath, files.Search("link", "marker", true).Status);
        Assert.Equal(FileOpStatus.InvalidPath, files.Rename("link/marker.pdf", "renamed.pdf").Status);
        Assert.Equal(FileOpStatus.InvalidPath, files.Delete("link/marker.pdf", false).Status);
        Assert.Equal(FileOpStatus.InvalidPath, files.Delete("link", true).Status);
        Assert.Equal(FileOpStatus.InvalidPath, files.Move("link", "", false).Status);
        Assert.Equal(FileOpStatus.InvalidPath, files.Copy("link", "", false).Status);
        Assert.Equal(FileOpStatus.InvalidPath, files.CreateFolder("link", "created").Status);
        Assert.Equal(FileOpStatus.InvalidPath, files.CreateFolder("", "link").Status);
        Assert.False((await files.UploadAsync("link", "new.pdf", new MemoryStream("%PDF-test"u8.ToArray()), true)).Uploaded);
        File.WriteAllText(Path.Combine(Managed, "own.pdf"), "%PDF-test");
        Assert.Equal(FileOpStatus.InvalidPath, files.Move("own.pdf", "link", true).Status);
        Assert.Equal(FileOpStatus.InvalidPath, files.Copy("own.pdf", "link", true).Status);
        Assert.Equal("%PDF-synthetic", File.ReadAllText(Path.Combine(Outside, "marker.pdf")));
        Assert.Single(Directory.GetFiles(Outside));
        using var rootLink = Service(link);
        Assert.Equal(FileOpStatus.InvalidPath, rootLink.List("").Status);
        Assert.NotNull(rootLink.GetStatus().Error);
    }

    [Fact]
    public void MappedUncScope_ProtectsInstancesAccessedThroughLocalRoot()
    {
        Directory.CreateDirectory(Path.Combine(Managed, "Alfa", "instance", "old"));
        var network = new NetworkOptions();
        network.Shares["data"] = new NetworkShareDefinition { Root = @"\\server\data", LocalMountPath = Managed };
        using var files = new FileManagerService(
            Options.Create(new FileManagerOptions { RootPath = Managed }),
            Options.Create(new ScopeSyncOptions { RootPath = @"\\server\data" }),
            new NetworkShareResolver(Options.Create(network)));
        Assert.Equal(FileOpStatus.ProtectedLocation, files.Delete("Alfa/instance", true).Status);
        Assert.Equal(FileOpStatus.ProtectedLocation, files.Rename("Alfa/instance/old", "changed").Status);
        Assert.True(Directory.Exists(Path.Combine(Managed, "Alfa", "instance", "old")));
    }

    [Fact]
    public void Status_ListAccessDenied_IsReportedAsUnreadable()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = new DirectoryInfo(Managed);
        var original = FileSystemAclExtensions.GetAccessControl(directory);
        var restricted = FileSystemAclExtensions.GetAccessControl(directory);
        restricted.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
            System.Security.Principal.WindowsIdentity.GetCurrent().User!,
            System.Security.AccessControl.FileSystemRights.ListDirectory,
            System.Security.AccessControl.AccessControlType.Deny));
        try
        {
            FileSystemAclExtensions.SetAccessControl(directory, restricted);
            using var files = Service();
            var status = files.GetStatus();
            Assert.True(status.RootExists);
            Assert.False(status.Readable);
            Assert.False(status.Writable);
            Assert.Contains("cannot be listed", status.Error);
        }
        finally
        {
            var restored = new System.Security.AccessControl.DirectorySecurity();
            restored.SetSecurityDescriptorBinaryForm(original.GetSecurityDescriptorBinaryForm(), System.Security.AccessControl.AccessControlSections.Access);
            FileSystemAclExtensions.SetAccessControl(directory, restored);
        }
        Assert.Empty(Directory.GetFiles(Managed));
    }

    [Fact]
    public async Task UploadAndRenameRejectLinkAtFinalDestination()
    {
        Link(Path.Combine(Managed, "target.pdf"), Outside);
        using var files = Service();
        File.WriteAllText(Path.Combine(Managed, "source.pdf"), "%PDF-test");
        Assert.Equal(FileOpStatus.InvalidPath, files.Rename("source.pdf", "target.pdf").Status);
        var upload = await files.UploadAsync("", "target.pdf", new MemoryStream("%PDF-test"u8.ToArray()), true);
        Assert.False(upload.Uploaded);
        Assert.Equal(FileUploadErrorCodes.InvalidName, upload.ErrorCode);
    }

    [Fact]
    public void ListingSearchAndCopySkipLinksIncludingCycles()
    {
        string source = Path.Combine(Managed, "source");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(Path.Combine(Managed, "destination"));
        File.WriteAllText(Path.Combine(source, "own.pdf"), "%PDF-test");
        Link(Path.Combine(source, "outside"), Outside);
        Link(Path.Combine(source, "cycle"), source);
        using var files = Service();
        Assert.Equal("own.pdf", Assert.Single(files.List("source").Items!).Name);
        Assert.Equal("own.pdf", Assert.Single(files.Search("source", ".pdf", true).Items!).Name);
        Assert.Equal(FileOpStatus.Ok, files.Copy("source", "destination", false).Status);
        Assert.Equal("own.pdf", Assert.Single(files.List("destination/source").Items!).Name);
        Assert.Empty(Directory.GetDirectories(Path.Combine(Managed, "destination", "source")));
    }

    [Fact]
    public void ScopeFallbackUsesAliasProfileAndKeepsConnectionUntilDisposed()
    {
        Directory.CreateDirectory(Path.Combine(Managed, "Alfa"));
        var resolver = new NetworkShareResolver(Options.Create(new NetworkOptions
        {
            Shares = { ["data"] = new NetworkShareDefinition { Root = @"\\server\share" } },
            CredentialProfiles = { ["scope"] = new NetworkCredentialProfile { Username = "audit-user", Password = "synthetic" } }
        }));
        var connector = new RecordingConnector(Managed);
        var service = new FileManagerService(Options.Create(new FileManagerOptions()),
            Options.Create(new ScopeSyncOptions { RootPath = "share:data", CredentialProfile = "scope" }), resolver, connector);
        try
        {
            Assert.Equal("Alfa", Assert.Single(service.List("").Items!).Name);
            Assert.Equal(Managed, service.GetStatus().RootPath);
            Assert.Equal(FileOpStatus.ProtectedLocation, service.Delete("Alfa", true).Status);
            Assert.Equal(@"\\server\share", connector.RequestedPath);
            Assert.Equal("audit-user", connector.Credentials!.Username);
            Assert.Equal(1, connector.ConnectCount);
            Assert.False(connector.Disposed);
        }
        finally { service.Dispose(); }
        Assert.True(connector.Disposed);
    }

    private sealed class RecordingConnector(string mappedPath) : INetworkShareConnector
    {
        public string? RequestedPath;
        public NetworkCredentials? Credentials;
        public int ConnectCount;
        public bool Disposed;
        public NetworkShareConnection Connect(string folder, NetworkCredentials? credentials)
        {
            RequestedPath = folder; Credentials = credentials; ConnectCount++;
            return new NetworkShareConnection(mappedPath, () => Disposed = true);
        }
    }

    public void Dispose()
    {
        // Remove links themselves first; recursive cleanup is confined to this fixture's unique temp root.
        foreach (var link in _links.AsEnumerable().Reverse()) Directory.Delete(link);
        string full = Path.GetFullPath(_fixture);
        Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), full, StringComparison.OrdinalIgnoreCase);
        Directory.Delete(full, true);
    }
}
