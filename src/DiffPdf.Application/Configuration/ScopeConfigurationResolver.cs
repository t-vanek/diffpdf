using DiffPdf.Application.Abstractions;
using DiffPdf.Core.Models;
using DiffPdf.Persistence;

namespace DiffPdf.Application.Configuration;

/// <inheritdoc />
public sealed class ScopeConfigurationResolver(IScopeConfigurationStore store) : IScopeConfigurationResolver
{
    public async Task<EffectiveConfiguration> ResolveForInstanceAsync(Guid branchId, Guid instanceId, CancellationToken ct = default)
    {
        var global = await store.GetGlobalAsync(ct);
        var branch = await store.GetByBranchAsync(branchId, ct);
        var instance = await store.GetByInstanceAsync(instanceId, ct);

        var (triggerCfg, triggerFrom) = ResolveInstance(
            instance?.TriggerSource ?? ConfigSource.Branch, instance?.TriggerConfig,
            branch?.TriggerSource ?? ConfigSource.Global, branch?.TriggerConfig,
            global?.TriggerConfig ?? new TriggerConfig());

        var (comparerCfg, comparerFrom) = ResolveInstance(
            instance?.ComparerSource ?? ConfigSource.Branch, instance?.ComparisonOptions,
            branch?.ComparerSource ?? ConfigSource.Global, branch?.ComparisonOptions,
            global?.ComparisonOptions ?? new ComparisonOptions());

        return new EffectiveConfiguration(triggerCfg, triggerFrom, comparerCfg, comparerFrom);
    }

    public async Task<EffectiveConfiguration> ResolveForBranchAsync(Guid branchId, CancellationToken ct = default)
    {
        var global = await store.GetGlobalAsync(ct);
        var branch = await store.GetByBranchAsync(branchId, ct);

        var (triggerCfg, triggerFrom) = ResolveBranch(
            branch?.TriggerSource ?? ConfigSource.Global, branch?.TriggerConfig, global?.TriggerConfig ?? new TriggerConfig());
        var (comparerCfg, comparerFrom) = ResolveBranch(
            branch?.ComparerSource ?? ConfigSource.Global, branch?.ComparisonOptions, global?.ComparisonOptions ?? new ComparisonOptions());

        return new EffectiveConfiguration(triggerCfg, triggerFrom, comparerCfg, comparerFrom);
    }

    public async Task<EffectiveConfiguration> ResolveGlobalAsync(CancellationToken ct = default)
    {
        var global = await store.GetGlobalAsync(ct);
        return new EffectiveConfiguration(
            global?.TriggerConfig ?? new TriggerConfig(), ConfigScopeLevel.Global,
            global?.ComparisonOptions ?? new ComparisonOptions(), ConfigScopeLevel.Global);
    }

    // ---- Inheritance rules (one chain, reused for triggers and comparers) ----

    /// <summary>Instance custom wins; otherwise defer to the branch chain (which yields Branch or Global).</summary>
    private static (T Payload, ConfigScopeLevel Level) ResolveInstance<T>(
        ConfigSource instanceSource, T? instanceCustom,
        ConfigSource branchSource, T? branchCustom, T globalPayload) where T : class =>
        instanceSource == ConfigSource.Custom && instanceCustom is not null
            ? (instanceCustom, ConfigScopeLevel.Instance)
            : ResolveBranch(branchSource, branchCustom, globalPayload);

    /// <summary>Branch custom wins; otherwise the global payload.</summary>
    private static (T Payload, ConfigScopeLevel Level) ResolveBranch<T>(
        ConfigSource branchSource, T? branchCustom, T globalPayload) where T : class =>
        branchSource == ConfigSource.Custom && branchCustom is not null
            ? (branchCustom, ConfigScopeLevel.Branch)
            : (globalPayload, ConfigScopeLevel.Global);
}
