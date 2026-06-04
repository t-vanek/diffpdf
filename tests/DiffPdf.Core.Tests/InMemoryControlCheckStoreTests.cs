using DiffPdf.Core.Models;
using DiffPdf.Core.Storage;
using DiffPdf.Persistence;

namespace DiffPdf.Core.Tests;

public class InMemoryControlCheckStoreTests
{
    private static ControlCheck Check(string key = "readiness", bool enabled = true) => new()
    {
        Id = Guid.NewGuid(),
        Key = key,
        Name = "Readiness",
        Type = CheckType.Readiness,
        ScopeKind = CheckScopeKind.Instance,
        BranchKey = "Alfa",
        InstanceKey = "LamaEnergy",
        IntervalSeconds = 300,
        Parameters = new Dictionary<string, string> { ["minPdf"] = "1" },
        Events = [NotificationEvent.ReadinessFailed],
        Enabled = enabled,
    };

    [Fact]
    public async Task Create_Get_GetByKey_List()
    {
        var store = new InMemoryControlCheckStore();
        var created = await store.CreateAsync(Check());

        Assert.Equal(1, created.Version);
        Assert.NotNull(await store.GetAsync(created.Id));
        Assert.NotNull(await store.GetByKeyAsync("readiness"));
        Assert.Single(await store.ListAsync());
    }

    [Fact]
    public async Task Create_DuplicateKey_Throws()
    {
        var store = new InMemoryControlCheckStore();
        await store.CreateAsync(Check("dup"));

        await Assert.ThrowsAsync<DuplicateKeyException>(() => store.CreateAsync(Check("dup")));
    }

    [Fact]
    public async Task ListEnabled_ExcludesDisabled()
    {
        var store = new InMemoryControlCheckStore();
        await store.CreateAsync(Check("a", enabled: true));
        await store.CreateAsync(Check("b", enabled: false));

        Assert.Single(await store.ListEnabledAsync());
    }

    [Fact]
    public async Task Update_BumpsVersion_StaleVersionConflicts()
    {
        var store = new InMemoryControlCheckStore();
        var created = await store.CreateAsync(Check());

        var updated = await store.UpdateAsync(created with { Name = "Renamed" }, created.Version);
        Assert.Equal(2, updated.Version);
        Assert.Equal("Renamed", updated.Name);

        await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => store.UpdateAsync(created with { Name = "Stale" }, created.Version)); // stale
    }

    [Fact]
    public async Task TouchLastRun_RecordsTimestampAndOutcome()
    {
        var store = new InMemoryControlCheckStore();
        var created = await store.CreateAsync(Check());
        var at = DateTimeOffset.UtcNow;

        await store.TouchLastRunAsync(created.Id, at, CheckRunOutcome.Failed);

        var reloaded = await store.GetAsync(created.Id);
        Assert.Equal(at, reloaded!.LastRunAt);
        Assert.Equal(CheckRunOutcome.Failed, reloaded.LastOutcome);
    }

    [Fact]
    public async Task Delete_RemovesCheck()
    {
        var store = new InMemoryControlCheckStore();
        var created = await store.CreateAsync(Check());

        Assert.True(await store.DeleteAsync(created.Id));
        Assert.Null(await store.GetAsync(created.Id));
        Assert.False(await store.DeleteAsync(created.Id));
    }
}

public class InMemoryControlCheckRunStoreTests
{
    [Fact]
    public async Task Add_ListByCheck_NewestFirst()
    {
        var store = new InMemoryControlCheckRunStore();
        var checkId = Guid.NewGuid();
        var older = new ControlCheckRun { Id = Guid.NewGuid(), CheckId = checkId, StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5), Outcome = CheckRunOutcome.Ok };
        var newer = new ControlCheckRun { Id = Guid.NewGuid(), CheckId = checkId, StartedAt = DateTimeOffset.UtcNow, Outcome = CheckRunOutcome.Failed, Detail = "boom" };

        await store.AddAsync(older);
        await store.AddAsync(newer);
        await store.AddAsync(new ControlCheckRun { Id = Guid.NewGuid(), CheckId = Guid.NewGuid(), Outcome = CheckRunOutcome.Ok }); // other check

        var runs = await store.ListByCheckAsync(checkId);
        Assert.Equal(2, runs.Count);
        Assert.Equal(newer.Id, runs[0].Id);
        Assert.Equal("boom", runs[0].Detail);
    }

    [Fact]
    public async Task ListByCheck_RespectsLimit()
    {
        var store = new InMemoryControlCheckRunStore();
        var checkId = Guid.NewGuid();
        for (int i = 0; i < 5; i++)
            await store.AddAsync(new ControlCheckRun { Id = Guid.NewGuid(), CheckId = checkId, StartedAt = DateTimeOffset.UtcNow.AddSeconds(i) });

        Assert.Equal(3, (await store.ListByCheckAsync(checkId, limit: 3)).Count);
    }
}
