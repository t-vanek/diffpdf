using DiffPdf.Application.Abstractions;
using DiffPdf.Core.Models;
using DiffPdf.Core.Storage;
using DiffPdf.Persistence;

namespace DiffPdf.Application.Configuration;

/// <summary>Fields for upserting a scope's configuration (sources + custom payloads, with a concurrency guard).</summary>
public sealed record UpsertScopeConfigInput
{
    public ConfigSource TriggerSource { get; init; }
    public TriggerConfig TriggerConfig { get; init; } = new();
    public ConfigSource ComparerSource { get; init; }
    public ComparisonOptions ComparisonOptions { get; init; } = new();

    /// <summary>Expected current version (optimistic concurrency). Null = overwrite the latest.</summary>
    public long? ExpectedVersion { get; init; }
}

/// <summary>A scope's stored configuration paired with the server-resolved effective config.</summary>
public sealed record ScopeConfigurationView(ScopeConfiguration Stored, EffectiveConfiguration Effective);

/// <summary>Validation failure surfaced as a clean error code + message (mirrors TriggerValidationException).</summary>
public sealed class ScopeConfigValidationException(string errorCode, string message) : Exception(message)
{
    public string ErrorCode { get; } = errorCode;
}

/// <summary>
/// Reads and upserts per-scope configuration for the API. Translates branch/instance keys to ids, validates
/// the source selectors against the level, ensures the row exists (idempotent provisioning), and returns the
/// stored settings together with the resolved effective config so the UI never computes inheritance itself.
/// </summary>
public interface IScopeConfigurationService
{
    Task<ScopeConfigurationView> GetGlobalAsync(CancellationToken ct = default);
    Task<ScopeConfigurationView?> GetBranchAsync(string branchKey, CancellationToken ct = default);
    Task<ScopeConfigurationView?> GetInstanceAsync(string branchKey, string instanceKey, CancellationToken ct = default);

    Task<ScopeConfigurationView> UpsertGlobalAsync(UpsertScopeConfigInput input, CancellationToken ct = default);
    Task<ScopeConfigurationView?> UpsertBranchAsync(string branchKey, UpsertScopeConfigInput input, CancellationToken ct = default);
    Task<ScopeConfigurationView?> UpsertInstanceAsync(string branchKey, string instanceKey, UpsertScopeConfigInput input, CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class ScopeConfigurationService(
    IScopeConfigurationStore store,
    IScopeConfigurationResolver resolver,
    IScopeConfigurationProvisioner provisioner,
    IBranchStore branches,
    IInstanceStore instances) : IScopeConfigurationService
{
    // ---------------- GET ----------------

    public async Task<ScopeConfigurationView> GetGlobalAsync(CancellationToken ct = default)
    {
        await provisioner.EnsureGlobalAsync(ct);
        var stored = await store.GetGlobalAsync(ct) ?? GlobalDefault();
        return new ScopeConfigurationView(stored, await resolver.ResolveGlobalAsync(ct));
    }

    public async Task<ScopeConfigurationView?> GetBranchAsync(string branchKey, CancellationToken ct = default)
    {
        var branch = await branches.GetByKeyAsync(branchKey, ct);
        if (branch is null) return null;

        await provisioner.EnsureBranchConfigAsync(branch.Id, ct);
        var stored = await store.GetByBranchAsync(branch.Id, ct) ?? BranchDefault(branch.Id);
        return new ScopeConfigurationView(stored, await resolver.ResolveForBranchAsync(branch.Id, ct));
    }

    public async Task<ScopeConfigurationView?> GetInstanceAsync(string branchKey, string instanceKey, CancellationToken ct = default)
    {
        var branch = await branches.GetByKeyAsync(branchKey, ct);
        if (branch is null) return null;
        var instance = await instances.GetByKeyAsync(branch.Id, instanceKey, ct);
        if (instance is null) return null;

        await provisioner.EnsureInstanceConfigAsync(branch.Id, instance.Id, ct);
        var stored = await store.GetByInstanceAsync(instance.Id, ct) ?? InstanceDefault(branch.Id, instance.Id);
        return new ScopeConfigurationView(stored, await resolver.ResolveForInstanceAsync(branch.Id, instance.Id, ct));
    }

    // ---------------- UPSERT ----------------

    public async Task<ScopeConfigurationView> UpsertGlobalAsync(UpsertScopeConfigInput input, CancellationToken ct = default)
    {
        await provisioner.EnsureGlobalAsync(ct);
        var current = await GetOrCreateAsync(() => store.GetGlobalAsync(ct), GlobalDefault, ct);
        // The global level is the root of every chain — its sources are always Custom.
        var updated = current with
        {
            TriggerSource = ConfigSource.Custom,
            TriggerConfig = input.TriggerConfig,
            ComparerSource = ConfigSource.Custom,
            ComparisonOptions = input.ComparisonOptions,
        };
        var saved = await store.UpdateAsync(updated, input.ExpectedVersion ?? current.Version, ct);
        return new ScopeConfigurationView(saved, await resolver.ResolveGlobalAsync(ct));
    }

    public async Task<ScopeConfigurationView?> UpsertBranchAsync(string branchKey, UpsertScopeConfigInput input, CancellationToken ct = default)
    {
        var branch = await branches.GetByKeyAsync(branchKey, ct);
        if (branch is null) return null;

        Validate(ConfigScopeLevel.Branch, input);
        await provisioner.EnsureBranchConfigAsync(branch.Id, ct);
        var current = await GetOrCreateAsync(() => store.GetByBranchAsync(branch.Id, ct), () => BranchDefault(branch.Id), ct);
        var updated = Apply(current, input);
        var saved = await store.UpdateAsync(updated, input.ExpectedVersion ?? current.Version, ct);
        return new ScopeConfigurationView(saved, await resolver.ResolveForBranchAsync(branch.Id, ct));
    }

    public async Task<ScopeConfigurationView?> UpsertInstanceAsync(string branchKey, string instanceKey, UpsertScopeConfigInput input, CancellationToken ct = default)
    {
        var branch = await branches.GetByKeyAsync(branchKey, ct);
        if (branch is null) return null;
        var instance = await instances.GetByKeyAsync(branch.Id, instanceKey, ct);
        if (instance is null) return null;

        Validate(ConfigScopeLevel.Instance, input);
        await provisioner.EnsureInstanceConfigAsync(branch.Id, instance.Id, ct);
        var current = await GetOrCreateAsync(() => store.GetByInstanceAsync(instance.Id, ct), () => InstanceDefault(branch.Id, instance.Id), ct);
        var updated = Apply(current, input);
        var saved = await store.UpdateAsync(updated, input.ExpectedVersion ?? current.Version, ct);
        return new ScopeConfigurationView(saved, await resolver.ResolveForInstanceAsync(branch.Id, instance.Id, ct));
    }

    // ---------------- helpers ----------------

    private static ScopeConfiguration Apply(ScopeConfiguration current, UpsertScopeConfigInput input) => current with
    {
        TriggerSource = input.TriggerSource,
        TriggerConfig = input.TriggerConfig,
        ComparerSource = input.ComparerSource,
        ComparisonOptions = input.ComparisonOptions,
    };

    // Read the scope's row, creating the default when absent. Tolerates losing a create race (with the
    // provisioner that ran just before, or a concurrent request): on a duplicate-key collision the row now
    // exists, so re-read it rather than surfacing the DuplicateKeyException as a 500 from this upsert path.
    private async Task<ScopeConfiguration> GetOrCreateAsync(
        Func<Task<ScopeConfiguration?>> get, Func<ScopeConfiguration> makeDefault, CancellationToken ct)
    {
        if (await get() is { } existing) return existing;
        try { return await store.CreateAsync(makeDefault(), ct); }
        catch (DuplicateKeyException) { return (await get())!; }
    }

    /// <summary>A branch may inherit from Global or be Custom; an instance from Branch or be Custom.</summary>
    private static void Validate(ConfigScopeLevel level, UpsertScopeConfigInput input)
    {
        EnsureAllowed(level, input.TriggerSource, "TriggerSource");
        EnsureAllowed(level, input.ComparerSource, "ComparerSource");

        static void EnsureAllowed(ConfigScopeLevel level, ConfigSource source, string field)
        {
            bool ok = level switch
            {
                ConfigScopeLevel.Branch => source is ConfigSource.Global or ConfigSource.Custom,
                ConfigScopeLevel.Instance => source is ConfigSource.Branch or ConfigSource.Custom,
                _ => source is ConfigSource.Custom,
            };
            if (!ok)
                throw new ScopeConfigValidationException(
                    "INVALID_SOURCE", $"Zdroj '{source}' není pro úroveň {level} povolen ({field}).");
        }
    }

    private static ScopeConfiguration GlobalDefault() => new()
    {
        Id = Guid.NewGuid(),
        Level = ConfigScopeLevel.Global,
        TriggerSource = ConfigSource.Custom,
        ComparerSource = ConfigSource.Custom,
    };

    private static ScopeConfiguration BranchDefault(Guid branchId) => new()
    {
        Id = Guid.NewGuid(),
        Level = ConfigScopeLevel.Branch,
        BranchId = branchId,
        TriggerSource = ConfigSource.Global,
        ComparerSource = ConfigSource.Global,
    };

    private static ScopeConfiguration InstanceDefault(Guid branchId, Guid instanceId) => new()
    {
        Id = Guid.NewGuid(),
        Level = ConfigScopeLevel.Instance,
        BranchId = branchId,
        InstanceId = instanceId,
        TriggerSource = ConfigSource.Branch,
        ComparerSource = ConfigSource.Branch,
    };
}
