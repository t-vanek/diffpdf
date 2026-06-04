using DiffPdf.Core.Models;
using DiffPdf.Core.Storage;
using DiffPdf.Persistence;

namespace DiffPdf.Core.Tests;

public class InMemoryScopeConfigurationStoreTests
{
    private static readonly Guid Branch1 = Guid.NewGuid();
    private static readonly Guid Branch2 = Guid.NewGuid();
    private static readonly Guid Inst1 = Guid.NewGuid();

    private static ScopeConfiguration Row(ConfigScopeLevel level, Guid? branchId = null, Guid? instanceId = null) => new()
    {
        Id = Guid.NewGuid(),
        Level = level,
        BranchId = branchId,
        InstanceId = instanceId,
    };

    [Fact]
    public async Task Create_And_GetByScope()
    {
        var store = new InMemoryScopeConfigurationStore();
        await store.CreateAsync(Row(ConfigScopeLevel.Global));
        await store.CreateAsync(Row(ConfigScopeLevel.Branch, Branch1));
        await store.CreateAsync(Row(ConfigScopeLevel.Instance, Branch1, Inst1));

        Assert.NotNull(await store.GetGlobalAsync());
        Assert.NotNull(await store.GetByBranchAsync(Branch1));
        Assert.NotNull(await store.GetByInstanceAsync(Inst1));
        Assert.Null(await store.GetByBranchAsync(Branch2));
    }

    [Fact]
    public async Task SecondGlobalRow_Throws()
    {
        var store = new InMemoryScopeConfigurationStore();
        await store.CreateAsync(Row(ConfigScopeLevel.Global));
        await Assert.ThrowsAsync<DuplicateKeyException>(() => store.CreateAsync(Row(ConfigScopeLevel.Global)));
    }

    [Fact]
    public async Task SecondRowForSameBranch_Throws()
    {
        var store = new InMemoryScopeConfigurationStore();
        await store.CreateAsync(Row(ConfigScopeLevel.Branch, Branch1));
        await Assert.ThrowsAsync<DuplicateKeyException>(() => store.CreateAsync(Row(ConfigScopeLevel.Branch, Branch1)));
    }

    [Fact]
    public async Task SecondRowForSameInstance_Throws()
    {
        var store = new InMemoryScopeConfigurationStore();
        await store.CreateAsync(Row(ConfigScopeLevel.Instance, Branch1, Inst1));
        await Assert.ThrowsAsync<DuplicateKeyException>(() => store.CreateAsync(Row(ConfigScopeLevel.Instance, Branch1, Inst1)));
    }

    [Fact]
    public async Task Update_BumpsVersion_AndGuardsConcurrency()
    {
        var store = new InMemoryScopeConfigurationStore();
        var created = await store.CreateAsync(Row(ConfigScopeLevel.Branch, Branch1));
        Assert.Equal(1, created.Version);

        var updated = await store.UpdateAsync(created with { TriggerSource = ConfigSource.Custom }, created.Version);
        Assert.Equal(2, updated.Version);
        Assert.Equal(ConfigSource.Custom, updated.TriggerSource);

        await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => store.UpdateAsync(updated with { TriggerSource = ConfigSource.Global }, expectedVersion: 1));
    }

    [Fact]
    public async Task Delete_ByBranch_And_ByInstance()
    {
        var store = new InMemoryScopeConfigurationStore();
        await store.CreateAsync(Row(ConfigScopeLevel.Branch, Branch1));
        await store.CreateAsync(Row(ConfigScopeLevel.Instance, Branch1, Inst1));

        Assert.True(await store.DeleteByBranchAsync(Branch1));
        Assert.False(await store.DeleteByBranchAsync(Branch1));
        Assert.Null(await store.GetByBranchAsync(Branch1));

        Assert.True(await store.DeleteByInstanceAsync(Inst1));
        Assert.Null(await store.GetByInstanceAsync(Inst1));
    }
}
