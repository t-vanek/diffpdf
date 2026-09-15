using DiffPdf.Core.Comparison;
using DiffPdf.Core.Network;
using DiffPdf.Messaging.ScopeSync;
using DiffPdf.Pdf.Network;
using DiffPdf.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DiffPdf.Core.Tests;

public class ScopeSyncServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "diffpdf-sync-" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryBranchStore _branches = new();
    private readonly InMemoryInstanceStore _instances = new();

    private ScopeSyncService Service(Action<ScopeSyncOptions>? configure = null, NetworkOptions? network = null)
    {
        var opt = new ScopeSyncOptions { RootPath = _root };
        configure?.Invoke(opt);
        var net = Options.Create(network ?? new NetworkOptions());
        var resolver = new NetworkShareResolver(net);
        var connector = new PlatformShareConnector(net, NullLogger<PlatformShareConnector>.Instance);
        var structure = new InstanceStructureService(resolver, connector, NullLogger<InstanceStructureService>.Instance);
        return new ScopeSyncService(resolver, connector, _branches, _instances, structure,
            new NoopAutomationProvisioner(), Options.Create(opt), NullLogger<ScopeSyncService>.Instance);
    }

    private void MakeInstanceFolders(string branch, string instance, params string[] subfolders)
    {
        foreach (var sub in subfolders)
            Directory.CreateDirectory(Path.Combine(_root, branch, instance, sub));
    }

    private string ConventionBase(string branch, string instance) =>
        InstanceFolders.Combine(InstanceFolders.Combine(_root, branch), instance);

    [Fact]
    public async Task DryRun_DiscoversFolders_RegistersNothing()
    {
        MakeInstanceFolders("branchA", "inst1", "old", "new"); // reports intentionally missing

        var report = await Service().SynchronizeAsync(apply: false);

        Assert.True(report.Reachable);
        Assert.True(report.Ok);
        var branch = Assert.Single(report.Branches);
        Assert.Equal("branchA", branch.Key);
        Assert.Equal(BranchSyncState.Registered, branch.State); // would register
        var inst = Assert.Single(branch.Instances);
        Assert.Equal(InstanceSyncState.Registered, inst.State);

        // Dry run: nothing persisted and no folders created.
        Assert.Empty(await _branches.ListAsync());
        Assert.False(Directory.Exists(Path.Combine(_root, "branchA", "inst1", "reports")));
        Assert.Equal(StructureItemState.Missing, inst.Structure!.Items.Single(i => i.Name == "reports").State);
    }

    [Fact]
    public async Task Apply_RegistersBranchAndInstance_AndCreatesSkeleton()
    {
        MakeInstanceFolders("branchA", "inst1", "old", "new");

        var report = await Service().SynchronizeAsync(apply: true);

        Assert.True(report.Ok);
        Assert.Equal(1, report.RegisteredBranches);
        Assert.Equal(1, report.RegisteredInstances);

        var branch = await _branches.GetByKeyAsync("branchA");
        Assert.NotNull(branch);
        var inst = await _instances.GetByKeyAsync(branch!.Id, "inst1");
        Assert.NotNull(inst);
        Assert.Equal(ConventionBase("branchA", "inst1"), inst!.BasePath);

        // The missing reports/ subfolder was created on disk.
        Assert.True(Directory.Exists(Path.Combine(_root, "branchA", "inst1", "reports")));
    }

    [Fact]
    public async Task Apply_IsIdempotent_SecondRunAllExisting()
    {
        MakeInstanceFolders("branchA", "inst1", "old", "new", "reports");

        await Service().SynchronizeAsync(apply: true);
        var second = await Service().SynchronizeAsync(apply: true);

        Assert.Equal(0, second.RegisteredBranches);
        Assert.Equal(0, second.RegisteredInstances);
        var branch = Assert.Single(second.Branches);
        Assert.Equal(BranchSyncState.Existing, branch.State);
        Assert.Equal(InstanceSyncState.Existing, Assert.Single(branch.Instances).State);
    }

    [Fact]
    public async Task InvalidFolderName_IsSkipped()
    {
        Directory.CreateDirectory(Path.Combine(_root, "bad name", "inst1")); // space -> invalid key

        var report = await Service().SynchronizeAsync(apply: true);

        var branch = Assert.Single(report.Branches);
        Assert.Equal(BranchSyncState.Skipped, branch.State);
        Assert.Empty(await _branches.ListAsync());
    }

    [Fact]
    public async Task AutoRegisterDisabled_ReportsOrphansWithoutRegistering()
    {
        MakeInstanceFolders("branchA", "inst1", "old", "new");

        var report = await Service(o => o.AutoRegister = false).SynchronizeAsync(apply: true);

        var branch = Assert.Single(report.Branches);
        Assert.Equal(BranchSyncState.Skipped, branch.State);
        Assert.Equal(InstanceSyncState.OrphanFolder, Assert.Single(branch.Instances).State);
        Assert.Empty(await _branches.ListAsync());
    }

    [Fact]
    public async Task MissingFolder_ForRegisteredInstance_IsCreatedOnApply()
    {
        Directory.CreateDirectory(_root);
        var branch = await _branches.CreateAsync("branchA", "branchA");
        await _instances.CreateAsync(branch.Id, "inst1", "inst1", ConventionBase("branchA", "inst1"), null);
        // Note: the on-disk folder is intentionally absent.

        var report = await Service().SynchronizeAsync(apply: true);

        var missing = Assert.Single(report.MissingFolders);
        Assert.Equal("inst1", missing.Key);
        Assert.Equal(InstanceSyncState.MissingFolder, missing.State);
        Assert.True(Directory.Exists(Path.Combine(_root, "branchA", "inst1", "old")));
    }

    [Fact]
    public async Task InstanceOutsideRoot_IsReportedNotTouched()
    {
        Directory.CreateDirectory(_root);
        var branch = await _branches.CreateAsync("ext", "ext");
        string outside = Path.Combine(Path.GetTempPath(), "diffpdf-outside-" + Guid.NewGuid().ToString("N"));
        await _instances.CreateAsync(branch.Id, "inst1", "inst1", outside, null);

        var report = await Service().SynchronizeAsync(apply: true);

        var outOfRoot = Assert.Single(report.OutOfRoot);
        Assert.Equal("inst1", outOfRoot.Key);
        Assert.Equal(InstanceSyncState.OutOfRoot, outOfRoot.State);
        Assert.Empty(report.MissingFolders);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* best effort */ }
    }

    [Fact]
    public async Task EnumerationAccessDenied_IsAnErrorNotAnEmptySuccessfulScan()
    {
        if (!OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(_root);
        var branch = await _branches.CreateAsync("branchA", "branchA");
        await _instances.CreateAsync(branch.Id, "inst1", "inst1", ConventionBase("branchA", "inst1"), null);
        var directory = new DirectoryInfo(_root);
        var original = System.IO.FileSystemAclExtensions.GetAccessControl(directory);
        var restricted = System.IO.FileSystemAclExtensions.GetAccessControl(directory);
        restricted.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
            System.Security.Principal.WindowsIdentity.GetCurrent().User!,
            System.Security.AccessControl.FileSystemRights.ListDirectory,
            System.Security.AccessControl.AccessControlType.Deny));
        try
        {
            System.IO.FileSystemAclExtensions.SetAccessControl(directory, restricted);
            var report = await Service().SynchronizeAsync(apply: true);
            Assert.False(report.Ok);
            Assert.NotNull(report.Error);
            Assert.Empty(report.MissingFolders);
        }
        finally
        {
            var restored = new System.Security.AccessControl.DirectorySecurity();
            restored.SetSecurityDescriptorBinaryForm(original.GetSecurityDescriptorBinaryForm(), System.Security.AccessControl.AccessControlSections.Access);
            System.IO.FileSystemAclExtensions.SetAccessControl(directory, restored);
        }
        Assert.False(Directory.Exists(ConventionBase("branchA", "inst1")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingKeysWithDifferentBasePath_AreReportedWithoutProvisioning(bool apply)
    {
        MakeInstanceFolders("branchA", "inst1", "old");
        var branch = await _branches.CreateAsync("branchA", "branchA");
        string oldPath = Path.Combine(_root, "previous-location");
        await _instances.CreateAsync(branch.Id, "inst1", "inst1", oldPath, null);

        var report = await Service().SynchronizeAsync(apply);

        var mismatch = Assert.Single(report.OutOfRoot);
        Assert.Equal(oldPath, mismatch.BasePath);
        Assert.Equal(InstanceSyncState.OutOfRoot, Assert.Single(Assert.Single(report.Branches).Instances).State);
        Assert.Empty(report.MissingFolders);
        Assert.False(Directory.Exists(oldPath));
        Assert.False(Directory.Exists(Path.Combine(ConventionBase("branchA", "inst1"), "reports")));
        Assert.Equal(oldPath, (await _instances.GetByKeyAsync(branch.Id, "inst1"))!.BasePath);
    }

    [Fact]
    public async Task ExistingUncBasePath_WithExplicitLocalMapping_IsTheSameInstance()
    {
        MakeInstanceFolders("branchA", "inst1", "old", "new", "reports");
        var branch = await _branches.CreateAsync("branchA", "branchA");
        string unc = @"\\server\data\branchA\inst1";
        await _instances.CreateAsync(branch.Id, "inst1", "inst1", unc, null);
        var network = new NetworkOptions();
        network.Shares["data"] = new NetworkShareDefinition { Root = @"\\server\data", LocalMountPath = _root };
        var report = await Service(network: network).SynchronizeAsync(apply: true);
        Assert.True(report.Ok);
        Assert.Empty(report.OutOfRoot);
        Assert.Empty(report.MissingFolders);
        Assert.Equal(0, report.RegisteredInstances);
        Assert.Equal(unc, (await _instances.GetByKeyAsync(branch.Id, "inst1"))!.BasePath);
    }
}
