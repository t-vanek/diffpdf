using DiffPdf.Core.Models;
using DiffPdf.Core.Storage;
using DiffPdf.Persistence;
using Microsoft.Extensions.Logging;

namespace DiffPdf.Messaging.Configuration;

/// <summary>
/// Auto-provisions the per-scope configuration rows so every scope has a row to inherit from / edit.
/// Idempotent and best-effort (mirrors <see cref="ControlPlane.IControlCheckProvisioner"/> and the trigger
/// provisioner): a row is created only when none exists, so manual edits are never overwritten, and
/// failures are logged rather than thrown. New branches/instances default to inheriting from their parent.
/// </summary>
public interface IScopeConfigurationProvisioner
{
    /// <summary>Ensure the single global config row exists (custom defaults). No-op if present.</summary>
    Task EnsureGlobalAsync(CancellationToken ct = default);

    /// <summary>Ensure a branch row exists, defaulting both sources to inherit (Global). No-op if present.</summary>
    Task EnsureBranchConfigAsync(Guid branchId, CancellationToken ct = default);

    /// <summary>Ensure an instance row exists, defaulting both sources to inherit (Branch). No-op if present.</summary>
    Task EnsureInstanceConfigAsync(Guid branchId, Guid instanceId, CancellationToken ct = default);

    /// <summary>Remove a branch's config row (called when the branch is deleted).</summary>
    Task RemoveBranchConfigAsync(Guid branchId, CancellationToken ct = default);

    /// <summary>Remove an instance's config row (called when the instance is deleted).</summary>
    Task RemoveInstanceConfigAsync(Guid instanceId, CancellationToken ct = default);

    /// <summary>Ensure the global row plus a row for every existing branch/instance (startup backfill).</summary>
    Task ProvisionExistingAsync(CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class ScopeConfigurationProvisioner(
    IScopeConfigurationStore store,
    IBranchStore branches,
    IInstanceStore instances,
    ILogger<ScopeConfigurationProvisioner> logger) : IScopeConfigurationProvisioner
{
    public async Task EnsureGlobalAsync(CancellationToken ct = default)
    {
        if (await store.GetGlobalAsync(ct) is not null)
            return;
        await EnsureAsync(new ScopeConfiguration
        {
            Id = Guid.NewGuid(),
            Level = ConfigScopeLevel.Global,
            // The global row is the root of every chain: it is always its own custom payload.
            TriggerSource = ConfigSource.Custom,
            ComparerSource = ConfigSource.Custom,
        }, "global", ct);
    }

    public async Task EnsureBranchConfigAsync(Guid branchId, CancellationToken ct = default)
    {
        if (await store.GetByBranchAsync(branchId, ct) is not null)
            return;
        await EnsureAsync(new ScopeConfiguration
        {
            Id = Guid.NewGuid(),
            Level = ConfigScopeLevel.Branch,
            BranchId = branchId,
            TriggerSource = ConfigSource.Global,
            ComparerSource = ConfigSource.Global,
        }, $"branch {branchId}", ct);
    }

    public async Task EnsureInstanceConfigAsync(Guid branchId, Guid instanceId, CancellationToken ct = default)
    {
        if (await store.GetByInstanceAsync(instanceId, ct) is not null)
            return;
        await EnsureAsync(new ScopeConfiguration
        {
            Id = Guid.NewGuid(),
            Level = ConfigScopeLevel.Instance,
            BranchId = branchId,
            InstanceId = instanceId,
            TriggerSource = ConfigSource.Branch,
            ComparerSource = ConfigSource.Branch,
        }, $"instance {instanceId}", ct);
    }

    public Task RemoveBranchConfigAsync(Guid branchId, CancellationToken ct = default) =>
        RemoveAsync(() => store.DeleteByBranchAsync(branchId, ct), $"branch {branchId}");

    public Task RemoveInstanceConfigAsync(Guid instanceId, CancellationToken ct = default) =>
        RemoveAsync(() => store.DeleteByInstanceAsync(instanceId, ct), $"instance {instanceId}");

    public async Task ProvisionExistingAsync(CancellationToken ct = default)
    {
        try
        {
            await EnsureGlobalAsync(ct);
            foreach (var branch in await branches.ListAsync(ct))
            {
                ct.ThrowIfCancellationRequested();
                await EnsureBranchConfigAsync(branch.Id, ct);
                foreach (var instance in await instances.ListAsync(branch.Id, ct))
                    await EnsureInstanceConfigAsync(branch.Id, instance.Id, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Auto-provision: backfill of scope configurations failed.");
        }
    }

    // Create the row only when absent; never overwrite an existing (possibly admin-edited) one.
    private async Task EnsureAsync(ScopeConfiguration desired, string scope, CancellationToken ct)
    {
        try
        {
            await store.CreateAsync(desired, ct);
            logger.LogInformation("Auto-provisioned scope configuration for {Scope}.", scope);
        }
        catch (DuplicateKeyException)
        {
            // Lost a race with another replica/request — a row now exists, which is the desired state.
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Auto-provision: could not ensure scope configuration for {Scope}.", scope);
        }
    }

    private async Task RemoveAsync(Func<Task<bool>> delete, string scope)
    {
        try
        {
            await delete();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Auto-provision: could not remove scope configuration for {Scope}.", scope);
        }
    }
}
