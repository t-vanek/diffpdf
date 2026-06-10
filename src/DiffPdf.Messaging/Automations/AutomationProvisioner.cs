using System.Globalization;
using DiffPdf.Application.Abstractions;
using DiffPdf.Core.Models;
using DiffPdf.Core.Storage;
using DiffPdf.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DiffPdf.Messaging.Automations;

/// <summary>
/// Default <see cref="IAutomationProvisioner"/>. Idempotent and non-destructive: an automation is created
/// only when its (stable, globally-unique) key is absent, so manual edits — including a deliberate
/// <c>Enabled = false</c> — are never overwritten. Best-effort throughout: failures are logged, never
/// thrown, so provisioning can never block a create request or an engine tick. Cadences and the retention
/// window come from <see cref="AutoProvisionOptions"/> (all defaulted). <c>NextRunAt</c> is computed at
/// creation so a fresh automation does not wait for the engine's seed pass.
/// </summary>
public sealed class AutomationProvisioner(
    IAutomationStore automations,
    IBranchStore branches,
    IOptions<AutomationEngineOptions> engineOptions,
    IOptions<ScopeSyncOptions> scopeSyncOptions,
    ILogger<AutomationProvisioner> logger) : IAutomationProvisioner
{
    internal const string HealthKey = "health";
    internal const string RetentionKey = "retention";
    internal const string DbRowRetentionKey = "db-row-retention";
    internal const string StructureSyncKey = "structure-sync";
    internal static string ReadinessKey(string branchKey) => $"readiness-{branchKey}";

    private AutoProvisionOptions Options => engineOptions.Value.AutoProvision;

    public Task EnsureBranchAutomationsAsync(string branchKey, CancellationToken ct = default)
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

        return EnsureAsync(new Automation
        {
            Id = Guid.NewGuid(),
            Key = key,
            Name = $"Readiness: {branchKey}",
            ScopeKind = AutomationScopeKind.Branch,
            BranchKey = branchKey,
            IntervalSeconds = Options.ReadinessIntervalSeconds,
            Steps = [new AutomationStep { Type = AutomationStepType.Readiness }],
            Events = [NotificationEvent.ReadinessFailed, NotificationEvent.AutomationRecovered],
        }, ct);
    }

    public async Task RemoveBranchAutomationsAsync(string branchKey, CancellationToken ct = default)
    {
        try
        {
            var existing = await automations.GetByKeyAsync(ReadinessKey(branchKey), ct);
            if (existing is not null)
                await automations.DeleteAsync(existing.Id, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Auto-provision: could not remove readiness automation for branch {Branch}.", branchKey);
        }
    }

    public async Task ProvisionBaselineAndExistingAsync(CancellationToken ct = default)
    {
        if (!Options.Enabled)
            return;

        // ---- Server-wide baseline. ----
        await EnsureAsync(new Automation
        {
            Id = Guid.NewGuid(), Key = HealthKey, Name = "Server health",
            ScopeKind = AutomationScopeKind.Global, IntervalSeconds = Options.HealthIntervalSeconds,
            Steps = [new AutomationStep { Type = AutomationStepType.Health }],
            Events = [NotificationEvent.HealthDegraded, NotificationEvent.AutomationRecovered],
        }, ct);

        await EnsureAsync(new Automation
        {
            Id = Guid.NewGuid(), Key = RetentionKey, Name = "Report retention",
            ScopeKind = AutomationScopeKind.Global, Cron = Options.RetentionCron,
            Steps =
            [
                new AutomationStep
                {
                    Type = AutomationStepType.Retention,
                    Parameters = new Dictionary<string, string>
                    {
                        ["retentionDays"] = Options.RetentionDays.ToString(CultureInfo.InvariantCulture),
                    },
                },
            ],
        }, ct);

        await EnsureAsync(new Automation
        {
            Id = Guid.NewGuid(), Key = DbRowRetentionKey, Name = "Database row retention",
            ScopeKind = AutomationScopeKind.Global, Cron = Options.DbRowRetentionCron,
            Steps =
            [
                new AutomationStep
                {
                    Type = AutomationStepType.DbRowRetention,
                    Parameters = new Dictionary<string, string>
                    {
                        ["retentionDays"] = Options.DbRowRetentionDays.ToString(CultureInfo.InvariantCulture),
                    },
                },
            ],
        }, ct);

        // StructureSync only makes sense when a sync root is configured — otherwise the executor is a no-op.
        if (!string.IsNullOrWhiteSpace(scopeSyncOptions.Value.RootPath))
        {
            await EnsureAsync(new Automation
            {
                Id = Guid.NewGuid(), Key = StructureSyncKey, Name = "Scope structure sync",
                ScopeKind = AutomationScopeKind.Global, IntervalSeconds = Options.StructureSyncIntervalSeconds,
                Steps = [new AutomationStep { Type = AutomationStepType.StructureSync }],
                Events = [NotificationEvent.StructureDrift, NotificationEvent.AutomationRecovered],
            }, ct);
        }

        // ---- Backfill: every existing branch gets its readiness automation. ----
        try
        {
            foreach (var branch in await branches.ListAsync(ct))
            {
                ct.ThrowIfCancellationRequested();
                await EnsureBranchAutomationsAsync(branch.Key, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Auto-provision: backfill of existing branches failed.");
        }
    }

    // Create the automation only when its key is absent; never overwrite an existing (possibly admin-edited) one.
    private async Task EnsureAsync(Automation desired, CancellationToken ct)
    {
        try
        {
            if (AutomationCadence.TryComputeNext(desired.Cron, desired.IntervalSeconds, DateTimeOffset.UtcNow, out var next, out _)
                && next is { } n)
                desired = desired with { NextRunAt = n };

            if (await automations.GetByKeyAsync(desired.Key, ct) is not null)
                return;
            await automations.CreateAsync(desired, ct);
            logger.LogInformation("Auto-provisioned automation '{Key}'.", desired.Key);
        }
        catch (DuplicateKeyException)
        {
            // Lost a race with another replica/request — the automation now exists, which is the desired state.
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Auto-provision: could not ensure automation '{Key}'.", desired.Key);
        }
    }
}
