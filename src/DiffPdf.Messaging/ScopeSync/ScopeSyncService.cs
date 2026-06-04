using DiffPdf.Core.Abstractions;
using DiffPdf.Core.Comparison;
using DiffPdf.Core.Models;
using DiffPdf.Core.Network;
using DiffPdf.Core.Storage;
using DiffPdf.Messaging.ControlPlane;
using DiffPdf.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DiffPdf.Messaging.ScopeSync;

/// <summary>
/// Reconciles a filesystem root (<c>&lt;root&gt;/&lt;branch&gt;/&lt;instance&gt;/{old,new,reports}</c>) with the
/// branch/instance records in the database, in both directions and non-destructively.
/// </summary>
public interface IScopeSyncService
{
    /// <summary>
    /// Detects branch/instance folders under the configured root and reconciles them with the database:
    /// registers folders not yet in the DB, ensures the old/new/reports skeleton, and reports DB instances
    /// whose folder is missing or whose base lies outside the root. Nothing is ever deleted.
    /// With <paramref name="apply"/> = false it is a dry run (report only).
    /// </summary>
    Task<ScopeSyncReport> SynchronizeAsync(bool apply, CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class ScopeSyncService(
    INetworkShareResolver resolver,
    INetworkShareConnector connector,
    IBranchStore branches,
    IInstanceStore instances,
    IInstanceStructureService structure,
    IControlCheckProvisioner provisioner,
    IOptions<ScopeSyncOptions> options,
    ILogger<ScopeSyncService> logger) : IScopeSyncService
{
    public async Task<ScopeSyncReport> SynchronizeAsync(bool apply, CancellationToken ct = default)
    {
        var opt = options.Value;
        string root = opt.RootPath ?? "";
        if (string.IsNullOrWhiteSpace(root))
            return new ScopeSyncReport(apply, "", false, [], [], [], "ScopeSync:RootPath is not configured.");

        ResolvedFolder resolved;
        try
        {
            resolved = resolver.Resolve(root, inlineCredentials: null, opt.CredentialProfile);
        }
        catch (NetworkConfigurationException ex)
        {
            return new ScopeSyncReport(apply, root, false, [], [], [], ex.Message);
        }

        try
        {
            using var connection = connector.Connect(resolved.Path, resolved.Credentials);
            string connectedRoot = connection.Path;

            if (apply)
                Directory.CreateDirectory(connectedRoot);
            if (!Directory.Exists(connectedRoot))
                return new ScopeSyncReport(apply, root, false, [], [], [], $"Root path does not exist: {connectedRoot}");

            var dbBranches = await branches.ListAsync(ct);
            var dbBranchByKey = dbBranches.ToDictionary(b => b.Key, StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<(string Branch, string Instance)>();

            // ---- Disk pass: discover branch/instance folders and reconcile + ensure structure. ----
            var branchResults = new List<BranchSyncResult>();
            foreach (var branchDir in SafeGetDirectories(connectedRoot))
            {
                ct.ThrowIfCancellationRequested();
                string branchName = Path.GetFileName(branchDir);

                if (!StorageKeyValidator.IsValidKey(branchName))
                {
                    branchResults.Add(new BranchSyncResult(branchName, branchName, BranchSyncState.Skipped, [], "Invalid branch key (folder name)."));
                    continue;
                }

                Branch? branch = dbBranchByKey.GetValueOrDefault(branchName);
                BranchSyncState branchState;
                string? branchDetail = null;
                if (branch is not null)
                {
                    branchState = BranchSyncState.Existing;
                }
                else if (!opt.AutoRegister)
                {
                    branchState = BranchSyncState.Skipped;
                    branchDetail = "Not registered (AutoRegister disabled).";
                }
                else if (apply)
                {
                    branch = await CreateBranchAsync(branchName, ct);
                    branchState = branch is not null ? BranchSyncState.Registered : BranchSyncState.Error;
                    // Newly registered from disk: give it the same readiness check as an API-created branch.
                    if (branch is not null)
                        await provisioner.EnsureBranchChecksAsync(branch.Key, ct);
                }
                else
                {
                    branchState = BranchSyncState.Registered; // would register (dry run)
                }

                var existingByKey = branch is not null
                    ? (await instances.ListAsync(branch.Id, ct)).ToDictionary(i => i.Key, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, ComparisonInstance>(StringComparer.OrdinalIgnoreCase);

                var instanceResults = new List<InstanceSyncResult>();
                foreach (var instanceDir in SafeGetDirectories(branchDir))
                {
                    ct.ThrowIfCancellationRequested();
                    string instanceName = Path.GetFileName(instanceDir);
                    string basePath = InstanceFolders.Combine(InstanceFolders.Combine(root, branchName), instanceName);

                    if (!StorageKeyValidator.IsValidKey(instanceName))
                    {
                        instanceResults.Add(new InstanceSyncResult(instanceName, instanceName, basePath, InstanceSyncState.Skipped, null, "Invalid instance key (folder name)."));
                        continue;
                    }
                    seen.Add((branchName, instanceName));

                    var existing = existingByKey.GetValueOrDefault(instanceName);
                    InstanceSyncState state;
                    string? detail = null;
                    ComparisonInstance? newlyRegistered = null;
                    if (existing is not null)
                    {
                        state = InstanceSyncState.Existing;
                    }
                    else if (!opt.AutoRegister)
                    {
                        state = InstanceSyncState.OrphanFolder;
                        detail = "Folder not registered (AutoRegister disabled).";
                    }
                    else if (apply && branch is not null)
                    {
                        newlyRegistered = await CreateInstanceAsync(branch.Id, instanceName, basePath, opt.CredentialProfile, ct);
                        state = newlyRegistered is not null ? InstanceSyncState.Registered : InstanceSyncState.Existing;
                    }
                    else
                    {
                        state = InstanceSyncState.Registered; // would register (dry run, or branch pending creation)
                    }

                    var structureReport = await EnsureOrInspectAsync(basePath, opt.CredentialProfile, apply && opt.AutoCreateFolders, ct);

                    instanceResults.Add(new InstanceSyncResult(instanceName, instanceName, basePath, state, structureReport, detail));
                }

                branchResults.Add(new BranchSyncResult(branchName, branchName, branchState, instanceResults, branchDetail));
            }

            // ---- DB pass: registered instances not found on disk this scan. ----
            var missingFolders = new List<InstanceSyncResult>();
            var outOfRoot = new List<InstanceSyncResult>();
            foreach (var dbBranch in dbBranches)
            {
                foreach (var inst in await instances.ListAsync(dbBranch.Id, ct))
                {
                    ct.ThrowIfCancellationRequested();
                    if (seen.Contains((dbBranch.Key, inst.Key)))
                        continue;

                    string expectedBase = InstanceFolders.Combine(InstanceFolders.Combine(root, dbBranch.Key), inst.Key);
                    if (!PathsEqual(inst.BasePath, expectedBase))
                    {
                        outOfRoot.Add(new InstanceSyncResult(inst.Key, "", inst.BasePath, InstanceSyncState.OutOfRoot, null,
                            $"{dbBranch.Key}: base path is outside the sync root."));
                        continue;
                    }

                    var structureReport = await EnsureOrInspectAsync(inst.BasePath, inst.CredentialProfile, apply && opt.AutoCreateFolders, ct);
                    missingFolders.Add(new InstanceSyncResult(inst.Key, "", inst.BasePath, InstanceSyncState.MissingFolder, structureReport,
                        $"{dbBranch.Key}: folder absent under root" + (apply && opt.AutoCreateFolders ? " (created)." : ".")));
                }
            }

            return new ScopeSyncReport(apply, root, true, branchResults, missingFolders, outOfRoot, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Scope sync failed for root {Root}", root);
            return new ScopeSyncReport(apply, root, false, [], [], [], ex.Message);
        }
    }

    private async Task<Branch?> CreateBranchAsync(string key, CancellationToken ct)
    {
        try { return await branches.CreateAsync(key, key, ct); }
        catch (DuplicateKeyException) { return await branches.GetByKeyAsync(key, ct); }
    }

    private async Task<ComparisonInstance?> CreateInstanceAsync(Guid branchId, string key, string basePath, string? credentialProfile, CancellationToken ct)
    {
        try { return await instances.CreateAsync(branchId, key, key, basePath, credentialProfile, ct); }
        catch (DuplicateKeyException) { return null; }
    }

    private async Task<InstanceStructureReport> EnsureOrInspectAsync(string basePath, string? credentialProfile, bool ensure, CancellationToken ct) =>
        ensure
            ? await structure.EnsureAsync(basePath, credentialProfile, includeFiles: false, ct)
            : await structure.InspectAsync(basePath, credentialProfile, includeFiles: false, ct);

    private IEnumerable<string> SafeGetDirectories(string path)
    {
        try { return Directory.GetDirectories(path); }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not enumerate directories under {Path}", path);
            return [];
        }
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
}
