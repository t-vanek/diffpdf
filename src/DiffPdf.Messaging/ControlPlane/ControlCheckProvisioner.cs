using System.Globalization;
using DiffPdf.Core.Models;
using DiffPdf.Core.Storage;
using DiffPdf.Messaging.ScopeSync;
using DiffPdf.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DiffPdf.Messaging.ControlPlane;

/// <summary>
/// Creates the standard control checks automatically so monitoring exists without manual setup: a
/// per-branch <see cref="CheckType.Readiness"/> check (which covers every enabled instance under the
/// branch) when a branch is created, and the server-wide baseline — <see cref="CheckType.Health"/>,
/// <see cref="CheckType.Retention"/> and, when a ScopeSync root is configured,
/// <see cref="CheckType.StructureSync"/> — plus a backfill of existing branches at startup.
/// </summary>
public interface IControlCheckProvisioner
{
    /// <summary>Idempotently ensure the per-branch Readiness check exists. No-op when auto-provision is off.</summary>
    Task EnsureBranchChecksAsync(string branchKey, CancellationToken ct = default);

    /// <summary>Remove the auto-provisioned per-branch check (called when a branch is deleted, to avoid orphans).</summary>
    Task RemoveBranchChecksAsync(string branchKey, CancellationToken ct = default);

    /// <summary>Ensure the server-wide baseline and a Readiness check for every existing branch. Runs once at startup.</summary>
    Task ProvisionBaselineAndExistingAsync(CancellationToken ct = default);
}

/// <summary>
/// Default <see cref="IControlCheckProvisioner"/>. Idempotent and non-destructive: a check is created only
/// when its (stable, globally-unique) key is absent, so manual edits — including a deliberate
/// <c>Enabled = false</c> — are never overwritten. Best-effort throughout: failures are logged, never
/// thrown, so provisioning can never block a create request or a control-plane tick. Cadences and the
/// retention window come from <see cref="AutoProvisionOptions"/> (all defaulted).
/// </summary>
public sealed class ControlCheckProvisioner(
    IControlCheckStore checks,
    IBranchStore branches,
    IOptions<ControlPlaneOptions> controlPlaneOptions,
    IOptions<ScopeSyncOptions> scopeSyncOptions,
    ILogger<ControlCheckProvisioner> logger) : IControlCheckProvisioner
{
    internal const string HealthKey = "health";
    internal const string RetentionKey = "retention";
    internal const string DbRowRetentionKey = "db-row-retention";
    internal const string StructureSyncKey = "structure-sync";
    internal static string ReadinessKey(string branchKey) => $"readiness-{branchKey}";

    private AutoProvisionOptions Options => controlPlaneOptions.Value.AutoProvision;

    public Task EnsureBranchChecksAsync(string branchKey, CancellationToken ct = default)
    {
        if (!Options.Enabled)
            return Task.CompletedTask;

        string key = ReadinessKey(branchKey);
        if (!StorageKeyValidator.IsValidKey(key))
        {
            logger.LogWarning(
                "Auto-provision: readiness key '{Key}' is invalid (branch key too long?); skipping branch {Branch}.",
                key, branchKey);
            return Task.CompletedTask;
        }

        return EnsureAsync(new ControlCheck
        {
            Id = Guid.NewGuid(),
            Key = key,
            Name = $"Readiness: {branchKey}",
            Type = CheckType.Readiness,
            ScopeKind = CheckScopeKind.Branch,
            BranchKey = branchKey,
            IntervalSeconds = Options.ReadinessIntervalSeconds,
            Events = [NotificationEvent.ReadinessFailed, NotificationEvent.CheckRecovered],
        }, ct);
    }

    public async Task RemoveBranchChecksAsync(string branchKey, CancellationToken ct = default)
    {
        try
        {
            var existing = await checks.GetByKeyAsync(ReadinessKey(branchKey), ct);
            if (existing is not null)
                await checks.DeleteAsync(existing.Id, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Auto-provision: could not remove readiness check for branch {Branch}.", branchKey);
        }
    }

    public async Task ProvisionBaselineAndExistingAsync(CancellationToken ct = default)
    {
        if (!Options.Enabled)
            return;

        // ---- Server-wide baseline. ----
        await EnsureAsync(new ControlCheck
        {
            Id = Guid.NewGuid(), Key = HealthKey, Name = "Server health", Type = CheckType.Health,
            ScopeKind = CheckScopeKind.Global, IntervalSeconds = Options.HealthIntervalSeconds,
            Events = [NotificationEvent.HealthDegraded, NotificationEvent.CheckRecovered],
        }, ct);

        await EnsureAsync(new ControlCheck
        {
            Id = Guid.NewGuid(), Key = RetentionKey, Name = "Report retention", Type = CheckType.Retention,
            ScopeKind = CheckScopeKind.Global, Cron = Options.RetentionCron,
            Parameters = new Dictionary<string, string>
            {
                ["retentionDays"] = Options.RetentionDays.ToString(CultureInfo.InvariantCulture),
            },
        }, ct);

        await EnsureAsync(new ControlCheck
        {
            Id = Guid.NewGuid(), Key = DbRowRetentionKey, Name = "Database row retention", Type = CheckType.DbRowRetention,
            ScopeKind = CheckScopeKind.Global, Cron = Options.DbRowRetentionCron,
            Parameters = new Dictionary<string, string>
            {
                ["retentionDays"] = Options.DbRowRetentionDays.ToString(CultureInfo.InvariantCulture),
            },
        }, ct);

        // StructureSync only makes sense when a sync root is configured — otherwise the executor is a no-op.
        if (!string.IsNullOrWhiteSpace(scopeSyncOptions.Value.RootPath))
        {
            await EnsureAsync(new ControlCheck
            {
                Id = Guid.NewGuid(), Key = StructureSyncKey, Name = "Scope structure sync", Type = CheckType.StructureSync,
                ScopeKind = CheckScopeKind.Global, IntervalSeconds = Options.StructureSyncIntervalSeconds,
                Events = [NotificationEvent.StructureDrift, NotificationEvent.CheckRecovered],
            }, ct);
        }

        // ---- Backfill: every existing branch gets its readiness check. ----
        try
        {
            foreach (var branch in await branches.ListAsync(ct))
            {
                ct.ThrowIfCancellationRequested();
                await EnsureBranchChecksAsync(branch.Key, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Auto-provision: backfill of existing branches failed.");
        }
    }

    // Create the check only when its key is absent; never overwrite an existing (possibly admin-edited) one.
    private async Task EnsureAsync(ControlCheck desired, CancellationToken ct)
    {
        try
        {
            if (await checks.GetByKeyAsync(desired.Key, ct) is not null)
                return;
            await checks.CreateAsync(desired, ct);
            logger.LogInformation("Auto-provisioned control check '{Key}' ({Type}).", desired.Key, desired.Type);
        }
        catch (DuplicateKeyException)
        {
            // Lost a race with another replica/request — the check now exists, which is the desired state.
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Auto-provision: could not ensure control check '{Key}'.", desired.Key);
        }
    }
}
