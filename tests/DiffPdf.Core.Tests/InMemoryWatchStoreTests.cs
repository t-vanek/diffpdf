using DiffPdf.Core.Models;
using DiffPdf.Persistence;

namespace DiffPdf.Core.Tests;

public class InMemoryWatchStoreTests
{
    private static FolderWatch Watch(Guid instanceId, bool enabled = true, int stability = 30) => new()
    {
        Id = Guid.NewGuid(),
        BranchId = Guid.NewGuid(),
        InstanceId = instanceId,
        BranchKey = "Alfa",
        InstanceKey = "Lama",
        StabilitySeconds = stability,
        Enabled = enabled,
    };

    [Fact]
    public async Task Upsert_InsertsThenUpdates_KeepingId()
    {
        var store = new InMemoryWatchStore();
        var inst = Guid.NewGuid();

        var created = await store.UpsertAsync(Watch(inst, stability: 30));
        Assert.Equal(1, created.Version);

        // A second upsert for the same instance updates in place (id preserved, version bumped).
        var updated = await store.UpsertAsync(Watch(inst, stability: 60));
        Assert.Equal(created.Id, updated.Id);
        Assert.Equal(60, updated.StabilitySeconds);
        Assert.Equal(2, updated.Version);

        Assert.Single(await store.ListAsync());
    }

    [Fact]
    public async Task GetByInstance_And_Delete()
    {
        var store = new InMemoryWatchStore();
        var inst = Guid.NewGuid();
        await store.UpsertAsync(Watch(inst));

        Assert.NotNull(await store.GetByInstanceAsync(inst));
        Assert.True(await store.DeleteByInstanceAsync(inst));
        Assert.Null(await store.GetByInstanceAsync(inst));
        Assert.False(await store.DeleteByInstanceAsync(inst));
    }

    [Fact]
    public async Task ListEnabled_ExcludesDisabled()
    {
        var store = new InMemoryWatchStore();
        await store.UpsertAsync(Watch(Guid.NewGuid(), enabled: true));
        await store.UpsertAsync(Watch(Guid.NewGuid(), enabled: false));

        Assert.Single(await store.ListEnabledAsync());
        Assert.Equal(2, (await store.ListAsync()).Count);
    }
}
