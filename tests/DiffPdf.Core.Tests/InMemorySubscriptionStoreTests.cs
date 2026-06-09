using DiffPdf.Core.Models;
using DiffPdf.Core.Storage;
using DiffPdf.Persistence;

namespace DiffPdf.Core.Tests;

public class InMemorySubscriptionStoreTests
{
    private static NotificationSubscription Sub(bool enabled = true) => new()
    {
        Id = Guid.NewGuid(),
        Name = "rule",
        Recipients = ["qa@x"],
        Events = [NotificationEvent.Completed],
        Enabled = enabled,
    };

    [Fact]
    public async Task Create_Get_List()
    {
        var store = new InMemorySubscriptionStore();
        var created = await store.CreateAsync(Sub());

        Assert.Equal(1, created.Version);
        Assert.NotNull(await store.GetAsync(created.Id));
        Assert.Single(await store.ListAsync());
    }

    [Fact]
    public async Task ListEnabled_ExcludesDisabled()
    {
        var store = new InMemorySubscriptionStore();
        await store.CreateAsync(Sub(enabled: true));
        await store.CreateAsync(Sub(enabled: false));

        Assert.Single(await store.ListEnabledAsync());
    }

    [Fact]
    public async Task Update_BumpsVersion_StaleVersionConflicts()
    {
        var store = new InMemorySubscriptionStore();
        var created = await store.CreateAsync(Sub());

        var updated = await store.UpdateAsync(created with { Name = "y" }, created.Version);
        Assert.Equal(2, updated.Version);

        await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => store.UpdateAsync(created with { Name = "z" }, created.Version)); // stale
    }

    [Fact]
    public async Task Delete_RemovesSubscription()
    {
        var store = new InMemorySubscriptionStore();
        var created = await store.CreateAsync(Sub());

        Assert.True(await store.DeleteAsync(created.Id));
        Assert.Null(await store.GetAsync(created.Id));
        Assert.False(await store.DeleteAsync(created.Id));
    }
}
