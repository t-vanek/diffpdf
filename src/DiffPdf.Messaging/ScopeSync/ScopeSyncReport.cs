using DiffPdf.Core.Network;

namespace DiffPdf.Messaging.ScopeSync;

/// <summary>How a branch folder reconciled against the database.</summary>
public enum BranchSyncState
{
    /// <summary>The branch already existed in the database.</summary>
    Existing,
    /// <summary>Registered from a disk folder (or, in a dry run, would be registered).</summary>
    Registered,
    /// <summary>Not acted on: invalid folder name, or AutoRegister disabled.</summary>
    Skipped,
    /// <summary>Reconciling this branch failed.</summary>
    Error,
}

/// <summary>How an instance reconciled against the database / disk.</summary>
public enum InstanceSyncState
{
    /// <summary>The instance already existed in the database.</summary>
    Existing,
    /// <summary>Registered from a disk folder (or, in a dry run, would be registered).</summary>
    Registered,
    /// <summary>A disk folder with no DB record, left unregistered (AutoRegister disabled).</summary>
    OrphanFolder,
    /// <summary>A registered instance under the root whose folder was absent on disk (created when applying).</summary>
    MissingFolder,
    /// <summary>A registered instance whose base path lies outside the sync root (informational; not touched).</summary>
    OutOfRoot,
    /// <summary>Not acted on: invalid folder name.</summary>
    Skipped,
    /// <summary>Reconciling this instance failed.</summary>
    Error,
}

/// <summary>Reconciliation result for a single instance.</summary>
public sealed record InstanceSyncResult(
    string Key,
    string FolderName,
    string BasePath,
    InstanceSyncState State,
    InstanceStructureReport? Structure,
    string? Detail);

/// <summary>Reconciliation result for a single branch and the instances under it.</summary>
public sealed record BranchSyncResult(
    string Key,
    string FolderName,
    BranchSyncState State,
    IReadOnlyList<InstanceSyncResult> Instances,
    string? Detail);

/// <summary>
/// What synchronizing the scope root found or did. With <c>apply = false</c> it is a dry run (detect/report only);
/// with <c>apply = true</c> branches/instances are registered and folders are created.
/// </summary>
public sealed record ScopeSyncReport(
    bool Applied,
    string Root,
    bool Reachable,
    IReadOnlyList<BranchSyncResult> Branches,
    IReadOnlyList<InstanceSyncResult> MissingFolders,
    IReadOnlyList<InstanceSyncResult> OutOfRoot,
    string? Error)
{
    /// <summary>Branches registered (or that would be registered in a dry run).</summary>
    public int RegisteredBranches => Branches.Count(b => b.State == BranchSyncState.Registered);

    /// <summary>Instances registered (or that would be registered in a dry run).</summary>
    public int RegisteredInstances => Branches.Sum(b => b.Instances.Count(i => i.State == InstanceSyncState.Registered));

    /// <summary>The root was reachable and the scan completed without a top-level error.</summary>
    public bool Ok => Reachable && Error is null;
}
