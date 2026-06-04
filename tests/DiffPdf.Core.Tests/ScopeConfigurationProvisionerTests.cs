using DiffPdf.Core.Models;
using DiffPdf.Messaging.Configuration;
using DiffPdf.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiffPdf.Core.Tests;

public class ScopeConfigurationProvisionerTests
{
    private static (InMemoryScopeConfigurationStore Configs, InMemoryBranchStore Branches, InMemoryInstanceStore Instances, ScopeConfigurationProvisioner Prov) Build()
    {
        var configs = new InMemoryScopeConfigurationStore();
        var branches = new InMemoryBranchStore();
        var instances = new InMemoryInstanceStore();
        return (configs, branches, instances,
            new ScopeConfigurationProvisioner(configs, branches, instances, NullLogger<ScopeConfigurationProvisioner>.Instance));
    }

    [Fact]
    public async Task EnsureGlobal_CreatesCustomRoot()
    {
        var (configs, _, _, prov) = Build();
        await prov.EnsureGlobalAsync();

        var g = await configs.GetGlobalAsync();
        Assert.NotNull(g);
        Assert.Equal(ConfigScopeLevel.Global, g!.Level);
        Assert.Equal(ConfigSource.Custom, g.TriggerSource);
        Assert.Equal(ConfigSource.Custom, g.ComparerSource);
    }

    [Fact]
    public async Task EnsureBranch_DefaultsToInheritGlobal()
    {
        var (configs, _, _, prov) = Build();
        var branchId = Guid.NewGuid();
        await prov.EnsureBranchConfigAsync(branchId);

        var b = await configs.GetByBranchAsync(branchId);
        Assert.NotNull(b);
        Assert.Equal(ConfigSource.Global, b!.TriggerSource);
        Assert.Equal(ConfigSource.Global, b.ComparerSource);
    }

    [Fact]
    public async Task EnsureInstance_DefaultsToInheritBranch()
    {
        var (configs, _, _, prov) = Build();
        var branchId = Guid.NewGuid();
        var instanceId = Guid.NewGuid();
        await prov.EnsureInstanceConfigAsync(branchId, instanceId);

        var i = await configs.GetByInstanceAsync(instanceId);
        Assert.NotNull(i);
        Assert.Equal(ConfigSource.Branch, i!.TriggerSource);
        Assert.Equal(ConfigSource.Branch, i.ComparerSource);
    }

    [Fact]
    public async Task Ensure_IsIdempotent_AndDoesNotOverwriteEdits()
    {
        var (configs, _, _, prov) = Build();
        var branchId = Guid.NewGuid();
        await prov.EnsureBranchConfigAsync(branchId);
        var original = await configs.GetByBranchAsync(branchId);
        await configs.UpdateAsync(original! with { TriggerSource = ConfigSource.Custom }, original!.Version);

        await prov.EnsureBranchConfigAsync(branchId); // must be a no-op

        var after = await configs.GetByBranchAsync(branchId);
        Assert.Equal(ConfigSource.Custom, after!.TriggerSource);
    }

    [Fact]
    public async Task ProvisionExisting_BackfillsGlobalBranchesAndInstances()
    {
        var (configs, branches, instances, prov) = Build();
        var b = await branches.CreateAsync("Alfa", "Alfa");
        var inst = await instances.CreateAsync(b.Id, "Lama", "Lama", "/base", null);

        await prov.ProvisionExistingAsync();

        Assert.NotNull(await configs.GetGlobalAsync());
        Assert.NotNull(await configs.GetByBranchAsync(b.Id));
        Assert.NotNull(await configs.GetByInstanceAsync(inst.Id));
    }

    [Fact]
    public async Task Remove_DeletesRows()
    {
        var (configs, _, _, prov) = Build();
        var branchId = Guid.NewGuid();
        var instanceId = Guid.NewGuid();
        await prov.EnsureBranchConfigAsync(branchId);
        await prov.EnsureInstanceConfigAsync(branchId, instanceId);

        await prov.RemoveBranchConfigAsync(branchId);
        await prov.RemoveInstanceConfigAsync(instanceId);

        Assert.Null(await configs.GetByBranchAsync(branchId));
        Assert.Null(await configs.GetByInstanceAsync(instanceId));
    }
}
