using DiffPdf.Application.Abstractions;
using DiffPdf.Core.Abstractions;
using DiffPdf.Core.Comparison;
using DiffPdf.Core.Models;
using DiffPdf.Core.Network;
using DiffPdf.Core.Storage;
using DiffPdf.Persistence;
using Microsoft.Extensions.Options;

namespace DiffPdf.Application.Scope;

/// <summary>Fields for creating an instance under a branch. <see cref="BasePath"/> is ignored when a ScopeSync root is configured.</summary>
public sealed record CreateInstanceInput(string Key, string Name, string BasePath, string? CredentialProfile);

/// <summary>Mutable instance fields (the Key is the immutable identity). <see cref="Version"/> guards concurrent edits.</summary>
public sealed record UpdateInstanceInput(string Name, string BasePath, string? CredentialProfile, bool Enabled, long Version);

/// <summary>A created instance plus the result of provisioning its folder skeleton (null when skipped).</summary>
public sealed record InstanceCreationResult(ComparisonInstance Instance, InstanceStructureReport? Structure);

/// <summary>Pre-flight readiness of an instance: the folder-skeleton inspection plus a dry-run pairing of old vs new.</summary>
public sealed record InstanceReadinessResult(
    InstanceStructureReport Structure,
    int Matched,
    int OnlyInOld,
    int OnlyInNew,
    IReadOnlyList<string> SampleOnlyInOld,
    IReadOnlyList<string> SampleOnlyInNew,
    bool Ready,
    string? Error);

/// <summary>
/// Instance management under a branch: create / update / delete plus folder-structure repair and the pre-flight
/// readiness probe. All validation, path/credential derivation, provisioning, audit and realtime events live
/// here. The endpoint maps the outcome (null = not found, <see cref="ScopeConflictException"/> = 409, …) to HTTP.
/// </summary>
public interface IInstanceService
{
    Task<IReadOnlyList<ComparisonInstance>?> ListAsync(string branchKey, CancellationToken ct = default);
    Task<ComparisonInstance?> GetAsync(string branchKey, string instanceKey, CancellationToken ct = default);
    Task<InstanceCreationResult> CreateAsync(string branchKey, CreateInstanceInput input, bool ensureStructure, bool autoProvision, string? actor, CancellationToken ct = default);
    Task<ComparisonInstance?> UpdateAsync(string branchKey, string instanceKey, UpdateInstanceInput input, string? actor, CancellationToken ct = default);
    Task<bool> DeleteAsync(string branchKey, string instanceKey, string? actor, CancellationToken ct = default);
    Task<InstanceStructureReport?> EnsureStructureAsync(string branchKey, string instanceKey, bool includeFiles, CancellationToken ct = default);
    Task<InstanceReadinessResult?> GetReadinessAsync(string branchKey, string instanceKey, int? sampleSize, bool includeFiles, CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class InstanceService(
    IBranchStore branches,
    IInstanceStore instances,
    IJobStore jobs,
    IInstanceStructureService structure,
    INetworkShareResolver shareResolver,
    INetworkDiscoveryService discovery,
    IControlCheckProvisioner provisioner,
    ITriggerProvisioner triggerProvisioner,
    IScopeConfigurationProvisioner configProvisioner,
    ITriggerEventPublisher events,
    IAuditLogStore audit,
    IOptions<ScopeSyncOptions> scopeSync) : IInstanceService
{
    public async Task<IReadOnlyList<ComparisonInstance>?> ListAsync(string branchKey, CancellationToken ct = default)
    {
        var branch = await branches.GetByKeyAsync(branchKey, ct);
        if (branch is null) return null;
        return await instances.ListAsync(branch.Id, ct);
    }

    public async Task<ComparisonInstance?> GetAsync(string branchKey, string instanceKey, CancellationToken ct = default) =>
        await ResolveInstanceAsync(branchKey, instanceKey, ct);

    public async Task<InstanceCreationResult> CreateAsync(
        string branchKey, CreateInstanceInput input, bool ensureStructure, bool autoProvision, string? actor, CancellationToken ct = default)
    {
        if (!StorageKeyValidator.IsValidKey(input.Key))
            throw new ScopeValidationException($"Invalid instance key '{input.Key}'.");

        var branch = await branches.GetByKeyAsync(branchKey, ct)
            ?? throw new ScopeNotFoundException($"Branch '{branchKey}' not found.");

        if (string.IsNullOrWhiteSpace(input.Name))
            throw new ScopeValidationException("Instance name must not be empty.");
        if ((await instances.ListAsync(branch.Id, ct)).Any(i => string.Equals(i.Name, input.Name.Trim(), StringComparison.OrdinalIgnoreCase)))
            throw new ScopeConflictException($"Instance name '{input.Name.Trim()}' is already used in this branch.");

        // When a structure root is configured, the base path is derived as <root>/<branch>/<instance>
        // (the caller's basePath is ignored). Otherwise the caller must supply an explicit base path.
        string root = (scopeSync.Value.RootPath ?? "").Trim();
        bool useRoot = root.Length > 0;
        if (!useRoot && string.IsNullOrWhiteSpace(input.BasePath))
            throw new ScopeValidationException("Instance basePath must not be empty (no ScopeSync root is configured).");

        string basePath = useRoot
            ? InstanceFolders.Combine(InstanceFolders.Combine(root, branchKey), input.Key)
            : input.BasePath;
        // Under a shared root the instance inherits the root's credential profile unless one is supplied.
        string? credentialProfile = useRoot && string.IsNullOrWhiteSpace(input.CredentialProfile)
            ? scopeSync.Value.CredentialProfile
            : input.CredentialProfile;

        ComparisonInstance created;
        try
        {
            created = await instances.CreateAsync(
                branch.Id, input.Key, input.Name, basePath, credentialProfile, ct);
        }
        catch (DuplicateKeyException ex)
        {
            throw new ScopeConflictException(ex.Message);
        }

        // Provision old/new/reports under the base path by default; best-effort so an
        // unreachable share does not undo the (valid) instance record.
        InstanceStructureReport? report = null;
        if (ensureStructure)
            report = await structure.EnsureAsync(created.BasePath, created.CredentialProfile, ct: ct);

        // Ensure the branch's readiness check exists (it already covers this new instance). Idempotent
        // safety net for branches that predate the branch-create hook; suppressed when autoProvision is false.
        if (autoProvision)
        {
            await provisioner.EnsureBranchChecksAsync(branchKey, ct);
            // After a correct branch+instance creation, provision the instance's default trigger (idempotent).
            await triggerProvisioner.EnsureDefaultTriggerAsync(branch.Id, branchKey, created.Id, created.Key, actor: null, ct);
            // Provision the instance's config row (defaults to inheriting from the branch).
            await configProvisioner.EnsureInstanceConfigAsync(branch.Id, created.Id, ct);
        }

        await events.PublishAsync(new TriggerEvent("instance.created", Guid.Empty,
            BranchId: branch.Id, InstanceId: created.Id, BranchKey: branchKey, InstanceKey: created.Key), ct);
        await audit.AddAsync(ScopeAudit.Entry("instance.created", actor, "Instance", created.Id, $"{branchKey}/{created.Key}"), ct);
        return new InstanceCreationResult(created, report);
    }

    public async Task<ComparisonInstance?> UpdateAsync(
        string branchKey, string instanceKey, UpdateInstanceInput input, string? actor, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(input.BasePath))
            throw new ScopeValidationException("Instance basePath must not be empty.");

        var branch = await branches.GetByKeyAsync(branchKey, ct);
        if (branch is null) return null;
        var existing = await instances.GetByKeyAsync(branch.Id, instanceKey, ct);
        if (existing is null) return null;

        var newName = string.IsNullOrWhiteSpace(input.Name) ? existing.Name : input.Name.Trim();
        if ((await instances.ListAsync(branch.Id, ct)).Any(i =>
                !string.Equals(i.Key, instanceKey, StringComparison.Ordinal)
                && string.Equals(i.Name, newName, StringComparison.OrdinalIgnoreCase)))
            throw new ScopeConflictException($"Instance name '{newName}' is already used in this branch.");

        var updated = existing with
        {
            Name = newName,
            BasePath = input.BasePath,
            CredentialProfile = string.IsNullOrWhiteSpace(input.CredentialProfile) ? null : input.CredentialProfile,
            Enabled = input.Enabled,
        };
        ComparisonInstance saved;
        try
        {
            saved = await instances.UpdateAsync(updated, input.Version, ct);
        }
        catch (ConcurrencyConflictException ex)
        {
            throw new ScopeConflictException(ex.Message);
        }

        await events.PublishAsync(new TriggerEvent("instance.status.changed", Guid.Empty,
            BranchId: branch.Id, InstanceId: saved.Id, BranchKey: branchKey, InstanceKey: saved.Key,
            Status: saved.Enabled ? "Enabled" : "Disabled"), ct);
        await audit.AddAsync(ScopeAudit.Entry("instance.updated", actor, "Instance", saved.Id,
            $"{branchKey}/{saved.Key} ({(saved.Enabled ? "Enabled" : "Disabled")})"), ct);
        return saved;
    }

    /// <summary>Deletes an instance. Returns false when the branch/instance does not exist; throws
    /// <see cref="ScopeConflictException"/> when it has an active job or any job history.</summary>
    public async Task<bool> DeleteAsync(string branchKey, string instanceKey, string? actor, CancellationToken ct = default)
    {
        var branch = await branches.GetByKeyAsync(branchKey, ct);
        if (branch is null) return false;
        var instance = await instances.GetByKeyAsync(branch.Id, instanceKey, ct);
        if (instance is null) return false;

        // Guard against orphaning the instance's jobs (active or historical).
        var instanceJobs = await jobs.ListAsync(new JobListQuery { BranchKey = branchKey, InstanceKey = instanceKey }, ct);
        if (instanceJobs.Any(j => ScopeAudit.ActiveStatuses.Contains(j.Status)))
            throw new ScopeConflictException($"Instance '{instanceKey}' has an active job; cancel or wait for it first.");
        if (instanceJobs.Count > 0)
            throw new ScopeConflictException($"Instance '{instanceKey}' has job history; it cannot be deleted (history is preserved).");

        await instances.DeleteByKeyAsync(branch.Id, instanceKey, ct);
        // Soft-delete the instance's triggers (run history is preserved) and remove its config row.
        await triggerProvisioner.SoftDeleteInstanceTriggersAsync(instance.Id, actor: null, ct);
        await configProvisioner.RemoveInstanceConfigAsync(instance.Id, ct);
        await events.PublishAsync(new TriggerEvent("instance.deleted", Guid.Empty,
            BranchId: branch.Id, InstanceId: instance.Id, BranchKey: branchKey, InstanceKey: instanceKey), ct);
        await audit.AddAsync(ScopeAudit.Entry("instance.deleted", actor, "Instance", instance.Id, $"{branchKey}/{instanceKey}"), ct);
        return true;
    }

    public async Task<InstanceStructureReport?> EnsureStructureAsync(
        string branchKey, string instanceKey, bool includeFiles, CancellationToken ct = default)
    {
        var instance = await ResolveInstanceAsync(branchKey, instanceKey, ct);
        if (instance is null) return null;
        return await structure.EnsureAsync(instance.BasePath, instance.CredentialProfile, includeFiles, ct);
    }

    public async Task<InstanceReadinessResult?> GetReadinessAsync(
        string branchKey, string instanceKey, int? sampleSize, bool includeFiles, CancellationToken ct = default)
    {
        var instance = await ResolveInstanceAsync(branchKey, instanceKey, ct);
        if (instance is null) return null;

        // 1) Inspect the skeleton: reachability, per-subfolder state and old/new PDF counts.
        var report = await structure.InspectAsync(instance.BasePath, instance.CredentialProfile, includeFiles, ct);
        if (!report.Reachable)
            return new InstanceReadinessResult(report, 0, 0, 0, [], [], false, report.Error);

        // 2) Derive the concrete old/new folders from the instance base (resolving a
        //    share alias / profile once), then dry-run the pairing to see what lines up.
        string oldFolder, newFolder;
        try
        {
            string basePath = shareResolver.Resolve(instance.BasePath, inlineCredentials: null, credentialProfile: instance.CredentialProfile).Path;
            oldFolder = InstanceFolders.Old(basePath);
            newFolder = InstanceFolders.New(basePath);
        }
        catch (NetworkConfigurationException ex)
        {
            return new InstanceReadinessResult(report, 0, 0, 0, [], [], false, ex.Message);
        }

        var pairing = await discovery.PreviewPairingAsync(
            oldFolder, newFolder,
            oldInlineCredentials: null, oldCredentialProfile: instance.CredentialProfile,
            newInlineCredentials: null, newCredentialProfile: instance.CredentialProfile,
            searchPattern: "*.pdf", recursive: true, sampleSize: sampleSize ?? 20, ct: ct);

        return new InstanceReadinessResult(
            report, pairing.Matched, pairing.OnlyInOld, pairing.OnlyInNew,
            pairing.SampleOnlyInOld, pairing.SampleOnlyInNew,
            report.HasComparableInputs, pairing.Error);
    }

    private async Task<ComparisonInstance?> ResolveInstanceAsync(string branchKey, string instanceKey, CancellationToken ct)
    {
        var branch = await branches.GetByKeyAsync(branchKey, ct);
        if (branch is null) return null;
        return await instances.GetByKeyAsync(branch.Id, instanceKey, ct);
    }
}
