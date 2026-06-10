using DiffPdf.Application.Abstractions;
using DiffPdf.Application.Automations;
using DiffPdf.Core.Models;
using DiffPdf.Persistence;

namespace DiffPdf.Core.Tests;

public class AutomationServiceTests
{
    private sealed class StubRunner : IAutomationRunner
    {
        public List<(Automation Automation, AutomationTriggerKind Trigger, string? Detail)> Runs { get; } = [];
        public Task<AutomationRun> RunAsync(Automation automation, AutomationTriggerKind trigger,
            string? triggerDetail, int chainDepth, CancellationToken ct = default)
        {
            Runs.Add((automation, trigger, triggerDetail));
            return Task.FromResult(new AutomationRun
            {
                Id = Guid.NewGuid(),
                AutomationId = automation.Id,
                Trigger = trigger,
                TriggerDetail = triggerDetail,
                CompletedAt = DateTimeOffset.UtcNow,
            });
        }
    }

    private static AutomationInput Input(
        string key = "auto",
        string name = "Auto",
        AutomationScopeKind scope = AutomationScopeKind.Global,
        string? branchKey = null,
        string? instanceKey = null,
        string? cron = null,
        int? interval = 300,
        IReadOnlyList<AutomationStep>? steps = null,
        int timeoutSeconds = 600,
        int maxAttempts = 1,
        int retryDelaySeconds = 30,
        int failureThreshold = 3,
        bool enabled = true) =>
        new(key, name, scope, branchKey, instanceKey, cron, interval,
            EventTriggers: [], EventDebounceSeconds: 60,
            Steps: steps ?? [new AutomationStep { Type = AutomationStepType.Health }],
            TimeoutSeconds: timeoutSeconds, MaxAttempts: maxAttempts, RetryDelaySeconds: retryDelaySeconds,
            FailureThreshold: failureThreshold, Events: [], Enabled: enabled);

    private static (AutomationService Service, InMemoryAutomationStore Store, StubRunner Runner, InMemoryAutomationRunStore Runs) Build()
    {
        var store = new InMemoryAutomationStore();
        var runner = new StubRunner();
        var runs = new InMemoryAutomationRunStore();
        return (new AutomationService(store, runner, runs), store, runner, runs);
    }

    [Theory]
    [InlineData("")]
    [InlineData("bad key")]
    [InlineData("a..b")]
    public async Task Create_InvalidKey_Throws(string key)
    {
        var (service, _, _, _) = Build();
        await Assert.ThrowsAsync<AutomationValidationException>(() => service.CreateAsync(Input(key: key)));
    }

    [Fact]
    public async Task Create_BranchScope_RequiresBranchKey()
    {
        var (service, _, _, _) = Build();
        await Assert.ThrowsAsync<AutomationValidationException>(
            () => service.CreateAsync(Input(scope: AutomationScopeKind.Branch)));
    }

    [Fact]
    public async Task Create_InstanceScope_RequiresBothKeys()
    {
        var (service, _, _, _) = Build();
        await Assert.ThrowsAsync<AutomationValidationException>(
            () => service.CreateAsync(Input(scope: AutomationScopeKind.Instance, branchKey: "Alfa")));
    }

    [Fact]
    public async Task Create_RequiresAtLeastOneStep()
    {
        var (service, _, _, _) = Build();
        await Assert.ThrowsAsync<AutomationValidationException>(
            () => service.CreateAsync(Input(steps: [])));
    }

    [Fact]
    public async Task Create_InvalidCron_Throws()
    {
        var (service, _, _, _) = Build();
        await Assert.ThrowsAsync<AutomationValidationException>(
            () => service.CreateAsync(Input(cron: "nope", interval: null)));
    }

    [Theory]
    [InlineData(0, 1, 30, 3)]      // timeout below range
    [InlineData(90_000, 1, 30, 3)] // timeout above range
    [InlineData(600, 0, 30, 3)]    // attempts below range
    [InlineData(600, 11, 30, 3)]   // attempts above range
    [InlineData(600, 1, -1, 3)]    // retry delay below range
    [InlineData(600, 1, 30, 0)]    // failure threshold below range
    public async Task Create_PolicyOutOfRange_Throws(int timeout, int attempts, int retryDelay, int threshold)
    {
        var (service, _, _, _) = Build();
        await Assert.ThrowsAsync<AutomationValidationException>(() => service.CreateAsync(
            Input(timeoutSeconds: timeout, maxAttempts: attempts, retryDelaySeconds: retryDelay, failureThreshold: threshold)));
    }

    [Fact]
    public async Task Create_ManualOnly_IsLegal_WithNoSchedule()
    {
        var (service, _, _, _) = Build();

        var automation = await service.CreateAsync(Input(cron: null, interval: null));

        Assert.Null(automation.NextRunAt);
        Assert.True(automation.Enabled);
    }

    [Fact]
    public async Task Create_WithInterval_SeedsNextRun_InTheFuture()
    {
        var (service, _, _, _) = Build();

        var automation = await service.CreateAsync(Input(interval: 300));

        Assert.NotNull(automation.NextRunAt);
        Assert.True(automation.NextRunAt > DateTimeOffset.UtcNow.AddSeconds(290));
    }

    [Fact]
    public async Task Create_BlankName_DefaultsToKey()
    {
        var (service, _, _, _) = Build();

        var automation = await service.CreateAsync(Input(key: "retention", name: " "));

        Assert.Equal("retention", automation.Name);
    }

    [Fact]
    public async Task Update_UnknownId_ReturnsNull()
    {
        var (service, _, _, _) = Build();
        Assert.Null(await service.UpdateAsync(Guid.NewGuid(), Input(), version: 1));
    }

    [Fact]
    public async Task Update_UnchangedCadence_PreservesThePhase()
    {
        var (service, _, _, _) = Build();
        var created = await service.CreateAsync(Input(interval: 300));

        var updated = await service.UpdateAsync(created.Id, Input(name: "Renamed", interval: 300), created.Version);

        Assert.Equal(created.NextRunAt, updated!.NextRunAt);
    }

    [Fact]
    public async Task Update_ChangedCadence_RecomputesNextRun()
    {
        var (service, _, _, _) = Build();
        var created = await service.CreateAsync(Input(interval: 300));

        var updated = await service.UpdateAsync(created.Id, Input(interval: 3600), created.Version);

        Assert.NotEqual(created.NextRunAt, updated!.NextRunAt);
        Assert.True(updated.NextRunAt > DateTimeOffset.UtcNow.AddSeconds(3500));
    }

    [Fact]
    public async Task Update_ScheduleRemoved_ClearsNextRun()
    {
        var (service, _, _, _) = Build();
        var created = await service.CreateAsync(Input(interval: 300));

        var updated = await service.UpdateAsync(created.Id, Input(cron: null, interval: null), created.Version);

        Assert.Null(updated!.NextRunAt);
    }

    [Fact]
    public async Task Update_ReEnabling_RecomputesNextRun()
    {
        var (service, store, _, _) = Build();
        var created = await service.CreateAsync(Input(interval: 300, enabled: false));
        // Simulate a stale due time left over from before the automation was disabled.
        var stale = created with { NextRunAt = DateTimeOffset.UtcNow.AddDays(-1) };
        var persisted = await store.UpdateAsync(stale, created.Version);

        var updated = await service.UpdateAsync(created.Id, Input(interval: 300, enabled: true), persisted.Version);

        Assert.True(updated!.NextRunAt > DateTimeOffset.UtcNow); // no surprise overdue fire on re-enable
    }

    [Fact]
    public async Task RunNow_UnknownId_ReturnsNull()
    {
        var (service, _, _, _) = Build();
        Assert.Null(await service.RunNowAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task RunNow_RunsTheManualPipeline()
    {
        var (service, _, runner, _) = Build();
        var created = await service.CreateAsync(Input());

        var run = await service.RunNowAsync(created.Id);

        Assert.NotNull(run);
        var recorded = Assert.Single(runner.Runs);
        Assert.Equal(AutomationTriggerKind.Manual, recorded.Trigger);
        Assert.Equal("manual", recorded.Detail);
    }

    [Fact]
    public async Task RunNow_DoesNotTouchTheSchedule()
    {
        var (service, store, _, _) = Build();
        var created = await service.CreateAsync(Input(interval: 300));

        await service.RunNowAsync(created.Id);

        Assert.Equal(created.NextRunAt, (await store.GetAsync(created.Id))!.NextRunAt);
    }

    [Fact]
    public async Task RunNow_WhileInFlight_ThrowsBusy()
    {
        var (service, store, runner, _) = Build();
        var created = await service.CreateAsync(Input());
        Assert.True(await store.TryBeginRunAsync(created.Id, DateTimeOffset.UtcNow, null, TimeSpan.FromMinutes(15)));

        await Assert.ThrowsAsync<AutomationBusyException>(() => service.RunNowAsync(created.Id));
        Assert.Empty(runner.Runs);
    }

    [Fact]
    public async Task ListRuns_UnknownId_ReturnsNull()
    {
        var (service, _, _, _) = Build();
        Assert.Null(await service.ListRunsAsync(Guid.NewGuid(), limit: 10));
    }

    [Fact]
    public async Task ListRuns_ReturnsHistory()
    {
        var (service, _, _, runs) = Build();
        var created = await service.CreateAsync(Input());
        await runs.AddAsync(new AutomationRun { Id = Guid.NewGuid(), AutomationId = created.Id });

        var history = await service.ListRunsAsync(created.Id, limit: 10);

        Assert.Single(history!);
    }
}
