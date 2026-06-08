namespace DiffPdf.Application.Abstractions;

/// <summary>Provisions and removes the per-branch readiness control check (idempotent; honours auto-provision).</summary>
public interface IControlCheckProvisioner
{
    /// <summary>Idempotently ensure the per-branch Readiness check exists. No-op when auto-provision is off.</summary>
    Task EnsureBranchChecksAsync(string branchKey, CancellationToken ct = default);

    /// <summary>Remove the auto-provisioned per-branch check (called when a branch is deleted, to avoid orphans).</summary>
    Task RemoveBranchChecksAsync(string branchKey, CancellationToken ct = default);

    /// <summary>Ensure the server-wide baseline and a Readiness check for every existing branch. Runs once at startup.</summary>
    Task ProvisionBaselineAndExistingAsync(CancellationToken ct = default);
}

/// <summary>Provisions and removes the per-scope configuration rows (global / branch / instance), defaulting to inherit.</summary>
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

/// <summary>Provisions an instance's default trigger and soft-deletes an instance's triggers on delete.</summary>
public interface ITriggerProvisioner
{
    Task EnsureDefaultTriggerAsync(Guid branchId, string branchKey, Guid instanceId, string instanceKey, string? actor, CancellationToken ct = default);

    /// <summary>Soft-deletes an instance's triggers when the instance is deleted (history is preserved).</summary>
    Task SoftDeleteInstanceTriggersAsync(Guid instanceId, string? actor, CancellationToken ct = default);

    /// <summary>Ensures a default trigger for every existing instance (startup backfill).</summary>
    Task ProvisionExistingAsync(CancellationToken ct = default);
}
