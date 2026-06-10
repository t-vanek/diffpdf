using DiffPdf.Core.Models;
using DiffPdf.Core.Storage;
using DiffPdf.Persistence;

namespace DiffPdf.Core.Tests;

public class InMemoryAutomationStoreTests
{
    private static Automation Auto(string key = "readiness", bool enabled = true) => new()
    {
        Id = Guid.NewGuid(),
        Key = key,
        Name = "Readiness",
        ScopeKind = AutomationScopeKind.Instance,
        BranchKey = "Alfa",
        InstanceKey = "LamaEnergy",
        IntervalSeconds = 300,
        Steps = [new AutomationStep { Type = AutomationStepType.Readiness, Parameters = new Dictionary<string, string> { ["minPdf"] = "1" } }],
        Events = [NotificationEvent.ReadinessFailed],
        Enabled = enabled,
    };

    [Fact]
    public async Task Create_Get_GetByKey_List()
    {
        var store = new InMemoryAutomationStore();
        var created = await store.CreateAsync(Auto());

        Assert.Equal(1, created.Version);
        Assert.NotNull(await store.GetAsync(created.Id));
        Assert.NotNull(await store.GetByKeyAsync("readiness"));
        Assert.Single(await store.ListAsync());
    }

    [Fact]
    public async Task Create_DuplicateKey_Throws()
    {
        var store = new InMemoryAutomationStore();
        await store.CreateAsync(Auto("dup"));

        await Assert.ThrowsAsync<DuplicateKeyException>(() => store.CreateAsync(Auto("dup")));
    }

    [Fact]
    public async Task ListEnabled_ExcludesDisabled()
    {
        var store = new InMemoryAutomationStore();
        await store.CreateAsync(Auto("a", enabled: true));
        await store.CreateAsync(Auto("b", enabled: false));

        Assert.Single(await store.ListEnabledAsync());
    }

    [Fact]
    public async Task Update_BumpsVersion_StaleVersionConflicts()
    {
        var store = new InMemoryAutomationStore();
        var created = await store.CreateAsync(Auto());

        var updated = await store.UpdateAsync(created with { Name = "Renamed" }, created.Version);
        Assert.Equal(2, updated.Version);
        Assert.Equal("Renamed", updated.Name);

        await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => store.UpdateAsync(created with { Name = "Stale" }, created.Version)); // stale
    }

    [Fact]
    public async Task Delete_RemovesAutomation()
    {
        var store = new InMemoryAutomationStore();
        var created = await store.CreateAsync(Auto());

        Assert.True(await store.DeleteAsync(created.Id));
        Assert.Null(await store.GetAsync(created.Id));
        Assert.False(await store.DeleteAsync(created.Id));
    }

    [Fact]
    public async Task TryBeginRun_ClaimsIdle_AdvancesNextRun()
    {
        var store = new InMemoryAutomationStore();
        var created = await store.CreateAsync(Auto());
        var now = DateTimeOffset.UtcNow;
        var next = now.AddMinutes(5);

        Assert.True(await store.TryBeginRunAsync(created.Id, now, next, TimeSpan.FromMinutes(15)));

        var reloaded = await store.GetAsync(created.Id);
        Assert.Equal(now, reloaded!.RunningSince);
        Assert.Equal(next, reloaded.NextRunAt);
    }

    [Fact]
    public async Task TryBeginRun_InFlight_IsRejected_AndDoesNotAdvanceNextRun()
    {
        var store = new InMemoryAutomationStore();
        var created = await store.CreateAsync(Auto());
        var now = DateTimeOffset.UtcNow;
        Assert.True(await store.TryBeginRunAsync(created.Id, now, now.AddMinutes(5), TimeSpan.FromMinutes(15)));

        Assert.False(await store.TryBeginRunAsync(created.Id, now.AddSeconds(10), now.AddMinutes(10), TimeSpan.FromMinutes(15)));

        var reloaded = await store.GetAsync(created.Id);
        Assert.Equal(now.AddMinutes(5), reloaded!.NextRunAt); // rejected claim left NextRunAt alone
    }

    [Fact]
    public async Task TryBeginRun_StaleClaim_IsReclaimed()
    {
        var store = new InMemoryAutomationStore();
        var created = await store.CreateAsync(Auto());
        var crashed = DateTimeOffset.UtcNow.AddHours(-1);
        Assert.True(await store.TryBeginRunAsync(created.Id, crashed, null, TimeSpan.FromMinutes(15)));

        var now = DateTimeOffset.UtcNow;
        Assert.True(await store.TryBeginRunAsync(created.Id, now, null, TimeSpan.FromMinutes(15)));

        Assert.Equal(now, (await store.GetAsync(created.Id))!.RunningSince);
    }

    [Fact]
    public async Task TryBeginRun_MissingAutomation_ReturnsFalse()
    {
        var store = new InMemoryAutomationStore();
        Assert.False(await store.TryBeginRunAsync(Guid.NewGuid(), DateTimeOffset.UtcNow, null, TimeSpan.FromMinutes(15)));
    }

    [Fact]
    public async Task CompleteRun_ClearsClaim_RecordsOutcomeAndFailures()
    {
        var store = new InMemoryAutomationStore();
        var created = await store.CreateAsync(Auto());
        var now = DateTimeOffset.UtcNow;
        Assert.True(await store.TryBeginRunAsync(created.Id, now, null, TimeSpan.FromMinutes(15)));

        var at = now.AddSeconds(30);
        await store.CompleteRunAsync(created.Id, at, AutomationRunOutcome.Failed, consecutiveFailures: 2);

        var reloaded = await store.GetAsync(created.Id);
        Assert.Null(reloaded!.RunningSince);
        Assert.Equal(at, reloaded.LastRunAt);
        Assert.Equal(AutomationRunOutcome.Failed, reloaded.LastOutcome);
        Assert.Equal(2, reloaded.ConsecutiveFailures);
    }

    [Fact]
    public async Task SetNextRunIfNull_SeedsOnlyOnce()
    {
        var store = new InMemoryAutomationStore();
        var created = await store.CreateAsync(Auto());
        var first = DateTimeOffset.UtcNow.AddMinutes(5);

        await store.SetNextRunIfNullAsync(created.Id, first);
        await store.SetNextRunIfNullAsync(created.Id, first.AddHours(1)); // no-op, already seeded

        Assert.Equal(first, (await store.GetAsync(created.Id))!.NextRunAt);
    }

    [Fact]
    public async Task TryClaimEventTrigger_Claims_ThenDebounces()
    {
        var store = new InMemoryAutomationStore();
        var created = await store.CreateAsync(Auto());
        var now = DateTimeOffset.UtcNow;
        var debounce = TimeSpan.FromSeconds(60);

        Assert.True(await store.TryClaimEventTriggerAsync(created.Id, now, debounce));
        Assert.False(await store.TryClaimEventTriggerAsync(created.Id, now.AddSeconds(30), debounce)); // debounced
        Assert.True(await store.TryClaimEventTriggerAsync(created.Id, now.AddSeconds(61), debounce)); // window passed
    }

    [Fact]
    public async Task TryClaimEventTrigger_DisabledOrMissing_ReturnsFalse()
    {
        var store = new InMemoryAutomationStore();
        var disabled = await store.CreateAsync(Auto(enabled: false));

        Assert.False(await store.TryClaimEventTriggerAsync(disabled.Id, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(60)));
        Assert.False(await store.TryClaimEventTriggerAsync(Guid.NewGuid(), DateTimeOffset.UtcNow, TimeSpan.FromSeconds(60)));
    }
}

public class InMemoryAutomationRunStoreTests
{
    [Fact]
    public async Task Add_ListByAutomation_NewestFirst()
    {
        var store = new InMemoryAutomationRunStore();
        var automationId = Guid.NewGuid();
        var older = new AutomationRun { Id = Guid.NewGuid(), AutomationId = automationId, StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5), Outcome = AutomationRunOutcome.Ok };
        var newer = new AutomationRun { Id = Guid.NewGuid(), AutomationId = automationId, StartedAt = DateTimeOffset.UtcNow, Outcome = AutomationRunOutcome.Failed, Detail = "boom" };

        await store.AddAsync(older);
        await store.AddAsync(newer);
        await store.AddAsync(new AutomationRun { Id = Guid.NewGuid(), AutomationId = Guid.NewGuid(), Outcome = AutomationRunOutcome.Ok }); // other automation

        var runs = await store.ListByAutomationAsync(automationId);
        Assert.Equal(2, runs.Count);
        Assert.Equal(newer.Id, runs[0].Id);
        Assert.Equal("boom", runs[0].Detail);
    }

    [Fact]
    public async Task ListByAutomation_RespectsLimit()
    {
        var store = new InMemoryAutomationRunStore();
        var automationId = Guid.NewGuid();
        for (int i = 0; i < 5; i++)
            await store.AddAsync(new AutomationRun { Id = Guid.NewGuid(), AutomationId = automationId, StartedAt = DateTimeOffset.UtcNow.AddSeconds(i) });

        Assert.Equal(3, (await store.ListByAutomationAsync(automationId, limit: 3)).Count);
    }

    [Fact]
    public async Task DeleteStartedBefore_PrunesOldRuns()
    {
        var store = new InMemoryAutomationRunStore();
        var automationId = Guid.NewGuid();
        var cutoff = DateTimeOffset.UtcNow.AddDays(-90);
        await store.AddAsync(new AutomationRun { Id = Guid.NewGuid(), AutomationId = automationId, StartedAt = cutoff.AddDays(-1) });
        await store.AddAsync(new AutomationRun { Id = Guid.NewGuid(), AutomationId = automationId, StartedAt = cutoff.AddDays(1) });

        Assert.Equal(1, await store.DeleteStartedBeforeAsync(cutoff));
        Assert.Single(await store.ListByAutomationAsync(automationId, limit: 10));
    }
}
