using DiffPdf.Application.Abstractions;
using DiffPdf.Core.Abstractions;
using DiffPdf.Core.Models;
using DiffPdf.Messaging.Automations;
using DiffPdf.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DiffPdf.Core.Tests;

public class AutomationEngineTickTests
{
    private sealed class RecordingRunner : IAutomationRunner
    {
        private readonly object _gate = new();
        public List<(Guid AutomationId, AutomationTriggerKind Trigger)> Runs { get; } = [];

        /// <summary>When set, every run blocks until the test releases it.</summary>
        public SemaphoreSlim? Block { get; init; }

        public async Task<AutomationRun> RunAsync(Automation automation, AutomationTriggerKind trigger,
            string? triggerDetail, int chainDepth, CancellationToken ct = default)
        {
            lock (_gate)
                Runs.Add((automation.Id, trigger));
            if (Block is not null)
                await Block.WaitAsync(ct);
            return new AutomationRun
            {
                Id = Guid.NewGuid(),
                AutomationId = automation.Id,
                Trigger = trigger,
                CompletedAt = DateTimeOffset.UtcNow,
            };
        }
    }

    private sealed class NoopAutomationProvisionerDouble : IAutomationProvisioner
    {
        public Task EnsureBranchAutomationsAsync(string branchKey, CancellationToken ct = default) => Task.CompletedTask;
        public Task RemoveBranchAutomationsAsync(string branchKey, CancellationToken ct = default) => Task.CompletedTask;
        public Task ProvisionBaselineAndExistingAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FixedWorkerId : IWorkerInstanceIdProvider
    {
        public string WorkerInstanceId => "test-worker";
    }

    private static Automation Auto(string key = "auto", DateTimeOffset? nextRunAt = null, string? cron = null, int? interval = 60) => new()
    {
        Id = Guid.NewGuid(),
        Key = key,
        Name = key,
        Cron = cron,
        IntervalSeconds = interval,
        Steps = [new AutomationStep { Type = AutomationStepType.Health }],
        NextRunAt = nextRunAt,
    };

    private static (AutomationEngineService Engine, InMemoryAutomationStore Store, RecordingRunner Runner) Build(
        int maxConcurrentRuns = 4, SemaphoreSlim? block = null)
    {
        var store = new InMemoryAutomationStore();
        var runner = new RecordingRunner { Block = block };

        var services = new ServiceCollection();
        services.AddSingleton<IAutomationStore>(store);
        services.AddSingleton<IAutomationRunner>(runner);
        services.AddSingleton<IAutomationProvisioner, NoopAutomationProvisionerDouble>();
        services.AddSingleton<ITriggerProvisioner, NoopTriggerProvisioner>();
        services.AddSingleton<IScopeConfigurationProvisioner, NoopScopeConfigurationProvisioner>();
        var provider = services.BuildServiceProvider();

        var worker = new FixedWorkerId();
        var engine = new AutomationEngineService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new InMemoryLeaderElection(worker),
            worker,
            new AutomationHeartbeat(),
            new AutomationMetrics(),
            Options.Create(new AutomationEngineOptions { MaxConcurrentRuns = maxConcurrentRuns }),
            NullLogger<AutomationEngineService>.Instance);
        return (engine, store, runner);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        Assert.True(condition(), "condition not met within the timeout");
    }

    [Fact]
    public async Task Tick_SeedsSchedule_WithoutFiring()
    {
        var (engine, store, runner) = Build();
        var created = await store.CreateAsync(Auto(interval: 60));
        Assert.Null(created.NextRunAt);

        await engine.TickAsync(CancellationToken.None);

        var reloaded = await store.GetAsync(created.Id);
        Assert.NotNull(reloaded!.NextRunAt);
        Assert.True(reloaded.NextRunAt > DateTimeOffset.UtcNow.AddSeconds(30));
        await Task.Delay(100);
        Assert.Empty(runner.Runs); // seeding never fires immediately
    }

    [Fact]
    public async Task Tick_FiresDueAutomation_AndAdvancesTheSchedule()
    {
        var (engine, store, runner) = Build();
        var created = await store.CreateAsync(Auto(nextRunAt: DateTimeOffset.UtcNow.AddSeconds(-5)));

        await engine.TickAsync(CancellationToken.None);
        await WaitUntilAsync(() => runner.Runs.Count == 1);

        Assert.Equal((created.Id, AutomationTriggerKind.Schedule), runner.Runs[0]);
        var reloaded = await store.GetAsync(created.Id);
        Assert.True(reloaded!.NextRunAt > DateTimeOffset.UtcNow); // advanced by the claim
    }

    [Fact]
    public async Task Tick_FreshClaim_PreventsDoubleFire_AndKeepsTheScheduleDue()
    {
        var (engine, store, runner) = Build();
        var due = DateTimeOffset.UtcNow.AddSeconds(-5);
        var created = await store.CreateAsync(Auto(nextRunAt: due));
        // A manual/event run holds the claim right now.
        Assert.True(await store.TryBeginRunAsync(created.Id, DateTimeOffset.UtcNow, null, TimeSpan.FromMinutes(15)));

        await engine.TickAsync(CancellationToken.None);
        await Task.Delay(200); // give a (wrong) fire a chance to surface

        Assert.Empty(runner.Runs);
        Assert.Equal(due, (await store.GetAsync(created.Id))!.NextRunAt); // still due — retried next tick
    }

    [Fact]
    public async Task Tick_StaleClaim_IsReclaimedAndFires()
    {
        var (engine, store, runner) = Build();
        var created = await store.CreateAsync(Auto(nextRunAt: DateTimeOffset.UtcNow.AddSeconds(-5)));
        // A crashed run left a claim far older than the automation's worst-case duration.
        Assert.True(await store.TryBeginRunAsync(created.Id, DateTimeOffset.UtcNow.AddHours(-2), null, TimeSpan.FromMinutes(15)));

        await engine.TickAsync(CancellationToken.None);
        await WaitUntilAsync(() => runner.Runs.Count == 1);
    }

    [Fact]
    public async Task Tick_RespectsMaxConcurrentRuns()
    {
        using var gate = new SemaphoreSlim(0);
        var (engine, store, runner) = Build(maxConcurrentRuns: 1, block: gate);
        await store.CreateAsync(Auto("a", nextRunAt: DateTimeOffset.UtcNow.AddSeconds(-5)));
        await store.CreateAsync(Auto("b", nextRunAt: DateTimeOffset.UtcNow.AddSeconds(-5)));

        await engine.TickAsync(CancellationToken.None);
        await WaitUntilAsync(() => runner.Runs.Count == 1);
        await Task.Delay(200);
        Assert.Single(runner.Runs); // the second automation found no free slot

        gate.Release(); // first run finishes → slot freed (asynchronously)
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (runner.Runs.Count < 2 && DateTime.UtcNow < deadline)
        {
            await engine.TickAsync(CancellationToken.None); // retried ticks pick the waiting automation up
            await Task.Delay(20);
        }
        Assert.Equal(2, runner.Runs.Count);
        gate.Release();
    }

    [Fact]
    public async Task Tick_InvalidCadence_IsIgnoredWithoutCrashing()
    {
        var (engine, store, runner) = Build();
        await store.CreateAsync(Auto(cron: "not a cron", interval: null));

        await engine.TickAsync(CancellationToken.None); // must not throw

        await Task.Delay(100);
        Assert.Empty(runner.Runs);
    }
}
