using DiffPdf.Core.Models;
using DiffPdf.Core.Storage;
using DiffPdf.Persistence;
using Microsoft.Extensions.Logging;

namespace DiffPdf.Messaging.Triggers;

/// <summary>
/// Auto-provisions the single default trigger of an instance. Idempotent and best-effort (mirrors the
/// control-check provisioner): a default is created only when none exists, and creation is audited.
/// </summary>
public interface ITriggerProvisioner
{
    Task EnsureDefaultTriggerAsync(Guid branchId, string branchKey, Guid instanceId, string instanceKey, string? actor, CancellationToken ct = default);

    /// <summary>Soft-deletes an instance's triggers when the instance is deleted (history is preserved).</summary>
    Task SoftDeleteInstanceTriggersAsync(Guid instanceId, string? actor, CancellationToken ct = default);

    /// <summary>Ensures a default trigger for every existing instance (startup backfill).</summary>
    Task ProvisionExistingAsync(CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class TriggerProvisioner(
    ITriggerStore triggers,
    IAuditLogStore audit,
    IBranchStore branches,
    IInstanceStore instances,
    ILogger<TriggerProvisioner> logger) : ITriggerProvisioner
{
    public const string DefaultTriggerName = "Výchozí spouštěč";

    public async Task EnsureDefaultTriggerAsync(Guid branchId, string branchKey, Guid instanceId, string instanceKey, string? actor, CancellationToken ct = default)
    {
        try
        {
            if (await triggers.GetDefaultForInstanceAsync(instanceId, ct) is not null)
                return; // already provisioned — never duplicate

            var created = await triggers.CreateAsync(new Trigger
            {
                Id = Guid.NewGuid(),
                BranchId = branchId,
                InstanceId = instanceId,
                BranchKey = branchKey,
                InstanceKey = instanceKey,
                Name = DefaultTriggerName,
                Description = "Automaticky vytvořený výchozí spouštěč pro spuštění porovnání instance.",
                ActionType = TriggerActionType.RunComparison,
                Status = TriggerStatus.Active,
                Enabled = true,
                IsDefault = true,
                Spec = new TriggerSpec(),
                CreatedBy = actor ?? "system",
            }, ct);

            await AuditCreatedAsync(created.Id, actor, $"default for {branchKey}/{instanceKey}", ct);
            logger.LogInformation("Auto-provisioned default trigger for {Branch}/{Instance}.", branchKey, instanceKey);
        }
        catch (DuplicateKeyException)
        {
            // Race with another replica/request — a default now exists, which is the desired state.
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Auto-provision: could not ensure default trigger for {Branch}/{Instance}.", branchKey, instanceKey);
        }
    }

    public async Task SoftDeleteInstanceTriggersAsync(Guid instanceId, string? actor, CancellationToken ct = default)
    {
        try
        {
            foreach (var t in await triggers.ListAsync(new TriggerQuery { InstanceId = instanceId }, ct))
            {
                await triggers.SoftDeleteAsync(t.Id, actor, ct);
                await audit.AddAsync(new AuditEntry
                {
                    Id = Guid.NewGuid(), Actor = actor, Source = nameof(JobSource.System),
                    Action = "trigger.deleted", EntityType = "Trigger", EntityId = t.Id.ToString(), Detail = "instance deleted",
                }, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Auto-provision: could not soft-delete triggers for instance {Instance}.", instanceId);
        }
    }

    public async Task ProvisionExistingAsync(CancellationToken ct = default)
    {
        try
        {
            foreach (var branch in await branches.ListAsync(ct))
            {
                ct.ThrowIfCancellationRequested();
                foreach (var instance in await instances.ListAsync(branch.Id, ct))
                    await EnsureDefaultTriggerAsync(branch.Id, branch.Key, instance.Id, instance.Key, "system", ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Auto-provision: backfill of default triggers failed.");
        }
    }

    private Task AuditCreatedAsync(Guid triggerId, string? actor, string detail, CancellationToken ct) =>
        audit.AddAsync(new AuditEntry
        {
            Id = Guid.NewGuid(), Actor = actor ?? "system", Source = nameof(JobSource.System),
            Action = "trigger.created", EntityType = "Trigger", EntityId = triggerId.ToString(), Detail = detail,
        }, ct);
}
