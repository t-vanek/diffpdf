using DiffPdf.Application.Abstractions;
using DiffPdf.Core.Abstractions;
using DiffPdf.Core.Comparison;
using DiffPdf.Core.Models;
using DiffPdf.Core.Network;
using DiffPdf.Core.Storage;
using DiffPdf.Persistence;
using Microsoft.Extensions.Options;

namespace DiffPdf.Application.Scope;

/// <summary>Fields for creating a branch.</summary>
public sealed record CreateBranchInput(string Key, string Name);

/// <summary>Mutable branch fields (the Key is the immutable identity). <see cref="Version"/> guards concurrent edits.</summary>
public sealed record UpdateBranchInput(string Name, bool Enabled, long Version);

/// <summary>A branch with its instance count and live run-queue state, for the list view.</summary>
public sealed record BranchSummaryView(Branch Branch, int InstanceCount, BranchQueueState Queue);

/// <summary>
/// Branch management: create / update / delete (with optional cascade) plus the list and one-call summary. All
/// validation, folder/check/config provisioning, audit and realtime events live here — the endpoint only maps
/// inputs and translates the outcome (null = not found, <see cref="ScopeConflictException"/> = 409, …) to HTTP.
/// </summary>
public interface IBranchService
{
    Task<IReadOnlyList<Branch>> ListAsync(CancellationToken ct = default);
    Task<IReadOnlyList<BranchSummaryView>> ListSummariesAsync(CancellationToken ct = default);
    Task<Branch?> GetByKeyAsync(string branchKey, CancellationToken ct = default);
    Task<Branch> CreateAsync(CreateBranchInput input, bool autoProvision, string? actor, CancellationToken ct = default);
    Task<Branch?> UpdateAsync(string branchKey, UpdateBranchInput input, string? actor, CancellationToken ct = default);
    Task<bool> DeleteAsync(string branchKey, bool cascade, string? actor, CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class BranchService(
    IBranchStore branches,
    IInstanceStore instances,
    IJobStore jobs,
    IFilePairTaskStore tasks,
    IControlCheckProvisioner provisioner,
    IScopeConfigurationProvisioner configProvisioner,
    ITriggerProvisioner triggerProvisioner,
    ITriggerEventPublisher events,
    IAuditLogStore audit,
    IInstanceStructureService structure,
    IBranchQueueDispatcher dispatcher,
    BranchDispatchLocks dispatchLocks,
    DiffPdfMetrics metrics,
    IOptions<ScopeSyncOptions> scopeSync) : IBranchService
{
    public async Task<IReadOnlyList<Branch>> ListAsync(CancellationToken ct = default) =>
        await branches.ListAsync(ct);

    public async Task<Branch?> GetByKeyAsync(string branchKey, CancellationToken ct = default) =>
        await branches.GetByKeyAsync(branchKey, ct);

    // One call returns every branch with its instance count + run-queue state, so the desktop list does not
    // make a per-branch queue round-trip (N+1): three batch queries total regardless of branch count.
    public async Task<IReadOnlyList<BranchSummaryView>> ListSummariesAsync(CancellationToken ct = default)
    {
        var list = await branches.ListAsync(ct);
        var counts = await instances.CountByBranchAsync(ct);
        var states = await dispatcher.GetStatesAsync(list, ct);
        return list.Select(b => new BranchSummaryView(
            b,
            counts.GetValueOrDefault(b.Id),
            states.GetValueOrDefault(b.Id) ?? new BranchQueueState(b.Key, b.QueuePaused, 0, 0, 0, 0, []))).ToList();
    }

    public async Task<Branch> CreateAsync(CreateBranchInput input, bool autoProvision, string? actor, CancellationToken ct = default)
    {
        if (!StorageKeyValidator.IsValidKey(input.Key))
            throw new ScopeValidationException($"Invalid branch key '{input.Key}'.");
        if (string.IsNullOrWhiteSpace(input.Name))
            throw new ScopeValidationException("Branch name must not be empty.");
        if ((await branches.ListAsync(ct)).Any(b => string.Equals(b.Name, input.Name.Trim(), StringComparison.OrdinalIgnoreCase)))
            throw new ScopeConflictException($"Branch name '{input.Name.Trim()}' is already used.");

        Branch created;
        try
        {
            created = await branches.CreateAsync(input.Key, input.Name, ct);
        }
        catch (DuplicateKeyException ex)
        {
            throw new ScopeConflictException(ex.Message);
        }

        // When a structure root is configured, create the branch folder <root>/<branch> on disk so the
        // tree is laid out top-down. Best-effort: an unreachable root does not undo the branch record.
        string root = (scopeSync.Value.RootPath ?? "").Trim();
        if (root.Length > 0)
            await structure.EnsureFolderAsync(InstanceFolders.Combine(root, created.Key), scopeSync.Value.CredentialProfile, ct);

        // Provision the branch's readiness check (covers every instance under it). Idempotent and
        // best-effort so it never fails the create; suppressed when autoProvision is false.
        if (autoProvision)
        {
            await provisioner.EnsureBranchChecksAsync(created.Key, ct);
            await configProvisioner.EnsureBranchConfigAsync(created.Id, ct);
        }

        await events.PublishAsync(new TriggerEvent("branch.created", Guid.Empty,
            BranchId: created.Id, BranchKey: created.Key), ct);
        await audit.AddAsync(ScopeAudit.Entry("branch.created", actor, "Branch", created.Id, created.Key), ct);
        return created;
    }

    public async Task<Branch?> UpdateAsync(string branchKey, UpdateBranchInput input, string? actor, CancellationToken ct = default)
    {
        var existing = await branches.GetByKeyAsync(branchKey, ct);
        if (existing is null) return null;

        var newName = string.IsNullOrWhiteSpace(input.Name) ? existing.Name : input.Name.Trim();
        if ((await branches.ListAsync(ct)).Any(b =>
                !string.Equals(b.Key, branchKey, StringComparison.Ordinal)
                && string.Equals(b.Name, newName, StringComparison.OrdinalIgnoreCase)))
            throw new ScopeConflictException($"Branch name '{newName}' is already used.");

        var updated = existing with
        {
            Name = newName,
            Enabled = input.Enabled,
        };
        Branch saved;
        try
        {
            saved = await branches.UpdateAsync(updated, input.Version, ct);
        }
        catch (ConcurrencyConflictException ex)
        {
            throw new ScopeConflictException(ex.Message);
        }

        await events.PublishAsync(new TriggerEvent("branch.status.changed", Guid.Empty,
            BranchId: saved.Id, BranchKey: saved.Key, Status: saved.Enabled ? "Enabled" : "Disabled"), ct);
        await audit.AddAsync(ScopeAudit.Entry("branch.updated", actor, "Branch", saved.Id,
            $"{saved.Key} ({(saved.Enabled ? "Enabled" : "Disabled")})"), ct);
        return saved;
    }

    /// <summary>Deletes a branch. Returns false when the branch does not exist; throws <see cref="ScopeConflictException"/>
    /// when it still holds instances or an active job (unless <paramref name="cascade"/>).</summary>
    public async Task<bool> DeleteAsync(string branchKey, bool cascade, string? actor, CancellationToken ct = default)
    {
        var branch = await branches.GetByKeyAsync(branchKey, ct);
        if (branch is null) return false;

        var branchInstances = await instances.ListAsync(branch.Id, ct);

        if (cascade)
        {
            // Caller explicitly opted in to deleting everything under the branch. Order matters for FKs:
            // file-pair tasks → jobs → instances (+ their triggers/config) → branch. Jobs are deleted in
            // pages so the count is unbounded.
            while (true)
            {
                var page = await jobs.ListAsync(new JobListQuery { BranchKey = branchKey, Limit = 500 }, ct);
                if (page.Count == 0) break;
                var ids = page.Select(j => j.Id).ToList();
                await tasks.DeleteForJobsAsync(ids, ct);
                await jobs.DeleteByIdsAsync(ids, ct);
            }
            foreach (var inst in branchInstances)
            {
                // Mirror the single-instance delete order (proven against the live FKs).
                await instances.DeleteByKeyAsync(branch.Id, inst.Key, ct);
                await triggerProvisioner.SoftDeleteInstanceTriggersAsync(inst.Id, actor: null, ct);
                await configProvisioner.RemoveInstanceConfigAsync(inst.Id, ct);
            }
        }
        else
        {
            // Guard 1: a branch that still holds instances must not be deleted (delete the instances first, or cascade).
            if (branchInstances.Count > 0)
                throw new ScopeConflictException(
                    $"Branch '{branchKey}' has {branchInstances.Count} instance(s); delete those first or pass ?cascade=true.");

            // Guard 2: do not delete while a job for this branch is still active.
            if (await ScopeAudit.HasActiveJobsAsync(jobs, branchKey, ct))
                throw new ScopeConflictException(
                    $"Branch '{branchKey}' has an active job (Draft/Queued/Running/Paused); cancel or wait for it first.");
        }

        await branches.DeleteByKeyAsync(branchKey, ct);
        // Remove the auto-provisioned readiness check, the branch config and the queue lock so nothing outlives the branch.
        await provisioner.RemoveBranchChecksAsync(branchKey, ct);
        await configProvisioner.RemoveBranchConfigAsync(branch.Id, ct);
        dispatchLocks.Remove(branch.Id);
        metrics.RemoveBranch(branch.Id);
        await events.PublishAsync(new TriggerEvent("branch.deleted", Guid.Empty,
            BranchId: branch.Id, BranchKey: branchKey), ct);
        await audit.AddAsync(ScopeAudit.Entry(cascade ? "branch.deleted.cascade" : "branch.deleted", actor, "Branch", branch.Id, branchKey), ct);
        return true;
    }
}
