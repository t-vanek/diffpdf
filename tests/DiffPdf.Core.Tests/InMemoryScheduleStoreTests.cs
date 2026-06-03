using DiffPdf.Core.Models;
using DiffPdf.Core.Storage;
using DiffPdf.Persistence;

namespace DiffPdf.Core.Tests;

public class InMemoryScheduleStoreTests
{
    private static ComparisonSchedule Sched(Guid instanceId, string key, bool enabled = true) => new()
    {
        Id = Guid.NewGuid(),
        BranchId = Guid.NewGuid(),
        InstanceId = instanceId,
        BranchKey = "Alfa",
        InstanceKey = "Lama",
        Key = key,
        Name = key,
        Cron = "0 2 * * *",
        Enabled = enabled,
    };

    [Fact]
    public async Task Create_Then_GetByKey()
    {
        var store = new InMemoryScheduleStore();
        var inst = Guid.NewGuid();
        var created = await store.CreateAsync(Sched(inst, "nightly"));

        var got = await store.GetByKeyAsync(inst, "nightly");
        Assert.NotNull(got);
        Assert.Equal(created.Id, got!.Id);
        Assert.Equal(1, got.Version);
    }

    [Fact]
    public async Task Create_DuplicateKey_Throws()
    {
        var store = new InMemoryScheduleStore();
        var inst = Guid.NewGuid();
        await store.CreateAsync(Sched(inst, "nightly"));
        await Assert.ThrowsAsync<DuplicateKeyException>(() => store.CreateAsync(Sched(inst, "nightly")));
    }

    [Fact]
    public async Task Update_BumpsVersion_StaleVersionConflicts()
    {
        var store = new InMemoryScheduleStore();
        var inst = Guid.NewGuid();
        var created = await store.CreateAsync(Sched(inst, "nightly"));

        var updated = await store.UpdateAsync(created with { Name = "renamed" }, created.Version);
        Assert.Equal(2, updated.Version);

        await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => store.UpdateAsync(created with { Name = "again" }, created.Version)); // stale
    }

    [Fact]
    public async Task ListEnabled_ExcludesDisabled()
    {
        var store = new InMemoryScheduleStore();
        var inst = Guid.NewGuid();
        await store.CreateAsync(Sched(inst, "on", enabled: true));
        await store.CreateAsync(Sched(inst, "off", enabled: false));

        var enabled = await store.ListEnabledAsync();
        Assert.Single(enabled);
        Assert.Equal("on", enabled[0].Key);
    }

    [Fact]
    public async Task Delete_RemovesSchedule()
    {
        var store = new InMemoryScheduleStore();
        var created = await store.CreateAsync(Sched(Guid.NewGuid(), "nightly"));

        Assert.True(await store.DeleteAsync(created.Id));
        Assert.Null(await store.GetAsync(created.Id));
        Assert.False(await store.DeleteAsync(created.Id));
    }
}
