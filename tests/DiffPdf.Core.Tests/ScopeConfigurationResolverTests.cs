using DiffPdf.Application.Configuration;
using DiffPdf.Core.Models;
using DiffPdf.Persistence;

namespace DiffPdf.Core.Tests;

/// <summary>The inheritance truth table for the per-scope configuration resolver (Global → Branch → Instance),
/// verified independently for the trigger and comparer chains.</summary>
public class ScopeConfigurationResolverTests
{
    private static readonly Guid Branch1 = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Inst1 = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static (InMemoryScopeConfigurationStore Store, ScopeConfigurationResolver Resolver) Build()
    {
        var store = new InMemoryScopeConfigurationStore();
        return (store, new ScopeConfigurationResolver(store));
    }

    private static Task Put(
        IScopeConfigurationStore store, ConfigScopeLevel level, Guid? branchId, Guid? instanceId,
        ConfigSource triggerSource, string searchPattern, ConfigSource comparerSource, int dpi) =>
        store.CreateAsync(new ScopeConfiguration
        {
            Id = Guid.NewGuid(),
            Level = level,
            BranchId = branchId,
            InstanceId = instanceId,
            TriggerSource = triggerSource,
            TriggerConfig = new TriggerConfig { SearchPattern = searchPattern },
            ComparerSource = comparerSource,
            ComparisonOptions = new ComparisonOptions { Dpi = dpi },
        });

    [Fact]
    public async Task NoRows_InstanceResolvesToGlobalDefaults()
    {
        var (_, resolver) = Build();

        var eff = await resolver.ResolveForInstanceAsync(Branch1, Inst1);

        Assert.Equal(ConfigScopeLevel.Global, eff.TriggerResolvedFrom);
        Assert.Equal(ConfigScopeLevel.Global, eff.ComparerResolvedFrom);
        Assert.Equal("*.pdf", eff.TriggerConfig.SearchPattern);
        Assert.Equal(150, eff.ComparisonOptions.Dpi);
    }

    [Fact]
    public async Task InstanceCustom_WinsOverBranchAndGlobal()
    {
        var (store, resolver) = Build();
        await Put(store, ConfigScopeLevel.Global, null, null, ConfigSource.Custom, "*.global", ConfigSource.Custom, 100);
        await Put(store, ConfigScopeLevel.Branch, Branch1, null, ConfigSource.Custom, "*.branch", ConfigSource.Custom, 200);
        await Put(store, ConfigScopeLevel.Instance, Branch1, Inst1, ConfigSource.Custom, "*.instance", ConfigSource.Custom, 300);

        var eff = await resolver.ResolveForInstanceAsync(Branch1, Inst1);

        Assert.Equal(ConfigScopeLevel.Instance, eff.TriggerResolvedFrom);
        Assert.Equal("*.instance", eff.TriggerConfig.SearchPattern);
        Assert.Equal(ConfigScopeLevel.Instance, eff.ComparerResolvedFrom);
        Assert.Equal(300, eff.ComparisonOptions.Dpi);
    }

    [Fact]
    public async Task InstanceBranchSource_BranchCustom_ResolvesToBranch()
    {
        var (store, resolver) = Build();
        await Put(store, ConfigScopeLevel.Global, null, null, ConfigSource.Custom, "*.global", ConfigSource.Custom, 100);
        await Put(store, ConfigScopeLevel.Branch, Branch1, null, ConfigSource.Custom, "*.branch", ConfigSource.Custom, 200);
        await Put(store, ConfigScopeLevel.Instance, Branch1, Inst1, ConfigSource.Branch, "*.ignored", ConfigSource.Branch, 999);

        var eff = await resolver.ResolveForInstanceAsync(Branch1, Inst1);

        Assert.Equal(ConfigScopeLevel.Branch, eff.TriggerResolvedFrom);
        Assert.Equal("*.branch", eff.TriggerConfig.SearchPattern);
        Assert.Equal(ConfigScopeLevel.Branch, eff.ComparerResolvedFrom);
        Assert.Equal(200, eff.ComparisonOptions.Dpi);
    }

    [Fact]
    public async Task InstanceBranchSource_BranchGlobal_ResolvesToGlobal()
    {
        var (store, resolver) = Build();
        await Put(store, ConfigScopeLevel.Global, null, null, ConfigSource.Custom, "*.global", ConfigSource.Custom, 100);
        await Put(store, ConfigScopeLevel.Branch, Branch1, null, ConfigSource.Global, "*.ignored", ConfigSource.Global, 999);
        await Put(store, ConfigScopeLevel.Instance, Branch1, Inst1, ConfigSource.Branch, "*.ignored2", ConfigSource.Branch, 888);

        var eff = await resolver.ResolveForInstanceAsync(Branch1, Inst1);

        Assert.Equal(ConfigScopeLevel.Global, eff.TriggerResolvedFrom);
        Assert.Equal("*.global", eff.TriggerConfig.SearchPattern);
        Assert.Equal(ConfigScopeLevel.Global, eff.ComparerResolvedFrom);
        Assert.Equal(100, eff.ComparisonOptions.Dpi);
    }

    [Fact]
    public async Task TriggerAndComparer_ResolveIndependently()
    {
        var (store, resolver) = Build();
        await Put(store, ConfigScopeLevel.Global, null, null, ConfigSource.Custom, "*.global", ConfigSource.Custom, 100);
        await Put(store, ConfigScopeLevel.Branch, Branch1, null, ConfigSource.Global, "*.branch", ConfigSource.Custom, 222);
        // Instance: trigger is Custom (own), comparer inherits Branch.
        await Put(store, ConfigScopeLevel.Instance, Branch1, Inst1, ConfigSource.Custom, "*.inst", ConfigSource.Branch, 999);

        var eff = await resolver.ResolveForInstanceAsync(Branch1, Inst1);

        Assert.Equal(ConfigScopeLevel.Instance, eff.TriggerResolvedFrom);
        Assert.Equal("*.inst", eff.TriggerConfig.SearchPattern);
        Assert.Equal(ConfigScopeLevel.Branch, eff.ComparerResolvedFrom);
        Assert.Equal(222, eff.ComparisonOptions.Dpi);
    }

    [Fact]
    public async Task MissingInstanceRow_InheritsBranch()
    {
        var (store, resolver) = Build();
        await Put(store, ConfigScopeLevel.Branch, Branch1, null, ConfigSource.Custom, "*.branch", ConfigSource.Custom, 200);
        // no instance row

        var eff = await resolver.ResolveForInstanceAsync(Branch1, Inst1);

        Assert.Equal(ConfigScopeLevel.Branch, eff.TriggerResolvedFrom);
        Assert.Equal("*.branch", eff.TriggerConfig.SearchPattern);
        Assert.Equal(200, eff.ComparisonOptions.Dpi);
    }

    [Fact]
    public async Task ResolveForBranch_CustomThenGlobal()
    {
        var (store, resolver) = Build();
        await Put(store, ConfigScopeLevel.Global, null, null, ConfigSource.Custom, "*.global", ConfigSource.Custom, 100);
        var branch = await store.CreateAsync(new ScopeConfiguration
        {
            Id = Guid.NewGuid(), Level = ConfigScopeLevel.Branch, BranchId = Branch1,
            TriggerSource = ConfigSource.Custom, TriggerConfig = new TriggerConfig { SearchPattern = "*.branch" },
            ComparerSource = ConfigSource.Custom, ComparisonOptions = new ComparisonOptions { Dpi = 200 },
        });

        var custom = await resolver.ResolveForBranchAsync(Branch1);
        Assert.Equal(ConfigScopeLevel.Branch, custom.TriggerResolvedFrom);
        Assert.Equal("*.branch", custom.TriggerConfig.SearchPattern);

        await store.UpdateAsync(branch with { TriggerSource = ConfigSource.Global, ComparerSource = ConfigSource.Global }, branch.Version);
        var global = await resolver.ResolveForBranchAsync(Branch1);
        Assert.Equal(ConfigScopeLevel.Global, global.TriggerResolvedFrom);
        Assert.Equal("*.global", global.TriggerConfig.SearchPattern);
        Assert.Equal(100, global.ComparisonOptions.Dpi);
    }
}
