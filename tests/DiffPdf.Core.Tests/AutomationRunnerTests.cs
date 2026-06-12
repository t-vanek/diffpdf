using DiffPdf.Application.Abstractions;
using DiffPdf.Core.Models;
using DiffPdf.Messaging.Automations;
using DiffPdf.Notifications;
using DiffPdf.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiffPdf.Core.Tests;

public class AutomationRunnerTests
{
    private sealed class StubExecutor(AutomationStepType type, Func<int, StepResult> resultByCall) : IAutomationStepExecutor
    {
        private int _calls;
        public StubExecutor(AutomationStepType type, StepResult result) : this(type, _ => result) { }
        public int Calls => _calls;
        public AutomationStepType Type => type;
        public Task<StepResult> ExecuteAsync(Automation automation, AutomationStep step, CancellationToken ct) =>
            Task.FromResult(resultByCall(Interlocked.Increment(ref _calls)));
    }

    private sealed class HangingExecutor(AutomationStepType type) : IAutomationStepExecutor
    {
        public AutomationStepType Type => type;
        public async Task<StepResult> ExecuteAsync(Automation automation, AutomationStep step, CancellationToken ct)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return StepResult.Ok("never reached");
        }
    }

    private sealed class RecordingDispatcher : INotificationDispatcher
    {
        public List<INotification> Sent { get; } = [];
        public Task DispatchAsync(INotification notification, CancellationToken ct = default)
        {
            Sent.Add(notification);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingEventSink : IAutomationEventSink
    {
        public List<(NotificationEvent Event, Guid? SourceId, int ChainDepth)> Published { get; } = [];
        public Task PublishAsync(NotificationEvent @event, string? branchKey, string? instanceKey, string detail,
            Guid? sourceAutomationId, int chainDepth, CancellationToken ct = default)
        {
            Published.Add((@event, sourceAutomationId, chainDepth));
            return Task.CompletedTask;
        }
    }

    private static Automation Auto(
        AutomationRunOutcome? lastOutcome = null,
        IReadOnlyList<AutomationStep>? steps = null,
        int maxAttempts = 1,
        int failureThreshold = 3,
        int consecutiveFailures = 0,
        int timeoutSeconds = 600) => new()
    {
        Id = Guid.NewGuid(),
        Key = "health",
        Name = "Health",
        IntervalSeconds = 60,
        Steps = steps ?? [new AutomationStep { Type = AutomationStepType.Health }],
        Events = [NotificationEvent.HealthDegraded],
        LastOutcome = lastOutcome,
        MaxAttempts = maxAttempts,
        RetryDelaySeconds = 0,
        FailureThreshold = failureThreshold,
        ConsecutiveFailures = consecutiveFailures,
        TimeoutSeconds = timeoutSeconds,
    };

    private static (AutomationRunner Runner, RecordingDispatcher Dispatcher, RecordingEventSink Sink,
        InMemoryAutomationStore Store, InMemoryAutomationRunStore Runs)
        Build(params IAutomationStepExecutor[] executors)
    {
        var store = new InMemoryAutomationStore();
        var runs = new InMemoryAutomationRunStore();
        var dispatcher = new RecordingDispatcher();
        var sink = new RecordingEventSink();
        using var metrics = new AutomationMetrics();
        var runner = new AutomationRunner(
            executors, store, runs, dispatcher, sink, metrics, NullLogger<AutomationRunner>.Instance);
        return (runner, dispatcher, sink, store, runs);
    }

    [Fact]
    public async Task Failure_RecordsRun_UpdatesState_AndNotifies()
    {
        var (runner, dispatcher, _, store, runs) = Build(
            new StubExecutor(AutomationStepType.Health, StepResult.Failed("db down")));
        var automation = await store.CreateAsync(Auto());

        var run = await runner.RunAsync(automation, AutomationTriggerKind.Schedule, null, 0);

        Assert.Equal(AutomationRunOutcome.Failed, run.Outcome);
        Assert.Equal("db down", run.Detail);
        Assert.Equal(1, run.Attempt);
        Assert.Single(run.StepResults);

        Assert.Single(await runs.ListByAutomationAsync(automation.Id));

        var reloaded = await store.GetAsync(automation.Id);
        Assert.Equal(AutomationRunOutcome.Failed, reloaded!.LastOutcome);
        Assert.Equal(1, reloaded.ConsecutiveFailures);
        Assert.Null(reloaded.RunningSince);

        var note = Assert.IsType<AutomationNotification>(Assert.Single(dispatcher.Sent));
        Assert.Equal(NotificationEvent.HealthDegraded, note.Event);
    }

    [Fact]
    public async Task Success_WithNoPriorFailure_DoesNotNotify()
    {
        var (runner, dispatcher, sink, store, _) = Build(
            new StubExecutor(AutomationStepType.Health, StepResult.Ok("healthy")));
        var automation = await store.CreateAsync(Auto());

        await runner.RunAsync(automation, AutomationTriggerKind.Schedule, null, 0);

        Assert.Empty(dispatcher.Sent);
        Assert.Empty(sink.Published);
    }

    [Fact]
    public async Task Success_AfterFailure_RaisesRecovered_AndResetsStreak()
    {
        var (runner, dispatcher, _, store, _) = Build(
            new StubExecutor(AutomationStepType.Health, StepResult.Ok("healthy")));
        var created = await store.CreateAsync(Auto());
        var failing = created with { LastOutcome = AutomationRunOutcome.Failed, ConsecutiveFailures = 2 };

        await runner.RunAsync(failing, AutomationTriggerKind.Schedule, null, 0);

        var note = Assert.IsType<AutomationNotification>(Assert.Single(dispatcher.Sent));
        Assert.Equal(NotificationEvent.AutomationRecovered, note.Event);
        Assert.Equal(0, (await store.GetAsync(created.Id))!.ConsecutiveFailures);
    }

    [Fact]
    public async Task UnknownStepType_RecordsFailedRun()
    {
        var (runner, _, _, store, _) = Build(); // no executors registered
        var automation = await store.CreateAsync(Auto());

        var run = await runner.RunAsync(automation, AutomationTriggerKind.Manual, "manual", 0);

        Assert.Equal(AutomationRunOutcome.Failed, run.Outcome);
        Assert.Contains("No executor", run.Detail);
    }

    [Fact]
    public async Task MultiStep_WarningContinues_AggregateIsWorst()
    {
        var (runner, _, _, store, _) = Build(
            new StubExecutor(AutomationStepType.Health, StepResult.Warning("meh")),
            new StubExecutor(AutomationStepType.Readiness, StepResult.Ok("ready")));
        var automation = await store.CreateAsync(Auto(steps:
        [
            new AutomationStep { Type = AutomationStepType.Health },
            new AutomationStep { Type = AutomationStepType.Readiness, Name = "Vstupy" },
        ]));

        var run = await runner.RunAsync(automation, AutomationTriggerKind.Schedule, null, 0);

        Assert.Equal(AutomationRunOutcome.Warning, run.Outcome);
        Assert.Equal(2, run.StepResults.Count);
        Assert.Equal("Vstupy", run.StepResults[1].Name);
        Assert.Contains("meh", run.Detail);
        Assert.Contains("ready", run.Detail);
    }

    [Fact]
    public async Task MultiStep_FailedStepStopsThePipeline()
    {
        var second = new StubExecutor(AutomationStepType.Readiness, StepResult.Ok("ready"));
        var (runner, _, _, store, _) = Build(
            new StubExecutor(AutomationStepType.Health, StepResult.Failed("down")),
            second);
        var automation = await store.CreateAsync(Auto(steps:
        [
            new AutomationStep { Type = AutomationStepType.Health },
            new AutomationStep { Type = AutomationStepType.Readiness },
        ]));

        var run = await runner.RunAsync(automation, AutomationTriggerKind.Schedule, null, 0);

        Assert.Equal(AutomationRunOutcome.Failed, run.Outcome);
        Assert.Single(run.StepResults); // fail fast: the second step never ran
        Assert.Equal(0, second.Calls);
    }

    [Fact]
    public async Task Retry_FailedThenOk_RecordsOneRowPerAttempt()
    {
        var (runner, _, _, store, runs) = Build(
            new StubExecutor(AutomationStepType.Health,
                call => call == 1 ? StepResult.Failed("flaky") : StepResult.Ok("recovered")));
        var automation = await store.CreateAsync(Auto(maxAttempts: 2));

        var run = await runner.RunAsync(automation, AutomationTriggerKind.Schedule, null, 0);

        Assert.Equal(AutomationRunOutcome.Ok, run.Outcome);
        Assert.Equal(2, run.Attempt);
        var history = await runs.ListByAutomationAsync(automation.Id);
        Assert.Equal(2, history.Count);
        Assert.Equal(0, (await store.GetAsync(automation.Id))!.ConsecutiveFailures);
    }

    [Fact]
    public async Task Timeout_FailsTheAttempt_WithTimeoutDetail()
    {
        var (runner, _, _, store, _) = Build(new HangingExecutor(AutomationStepType.Health));
        var automation = await store.CreateAsync(Auto(timeoutSeconds: 1));

        var run = await runner.RunAsync(automation, AutomationTriggerKind.Schedule, null, 0);

        Assert.Equal(AutomationRunOutcome.Failed, run.Outcome);
        Assert.Contains("timed out after 1s", run.Detail);
    }

    [Fact]
    public async Task Escalation_RaisedExactlyOnce_WhenStreakCrossesThreshold()
    {
        var (runner, dispatcher, _, store, _) = Build(
            new StubExecutor(AutomationStepType.Health, StepResult.Failed("down")));
        var created = await store.CreateAsync(Auto(failureThreshold: 2));

        // Streak 0 → 1: below the threshold, only the configured event.
        await runner.RunAsync(created, AutomationTriggerKind.Schedule, null, 0);
        Assert.DoesNotContain(dispatcher.Sent, n => n.Event == NotificationEvent.AutomationFailing);

        // Streak 1 → 2: crosses the threshold, escalates once.
        await runner.RunAsync(created with { ConsecutiveFailures = 1, LastOutcome = AutomationRunOutcome.Failed },
            AutomationTriggerKind.Schedule, null, 0);
        Assert.Single(dispatcher.Sent, n => n.Event == NotificationEvent.AutomationFailing);

        // Streak 2 → 3: already past the threshold, no repeat escalation.
        await runner.RunAsync(created with { ConsecutiveFailures = 2, LastOutcome = AutomationRunOutcome.Failed },
            AutomationTriggerKind.Schedule, null, 0);
        Assert.Single(dispatcher.Sent, n => n.Event == NotificationEvent.AutomationFailing);
    }

    [Fact]
    public async Task RaisedEvents_FlowIntoTheEventSink_WithSourceAndDepth()
    {
        var (runner, _, sink, store, _) = Build(
            new StubExecutor(AutomationStepType.Health, StepResult.Failed("down")));
        var automation = await store.CreateAsync(Auto());

        await runner.RunAsync(automation, AutomationTriggerKind.Event, "event:Failed", chainDepth: 1);

        var published = Assert.Single(sink.Published);
        Assert.Equal(NotificationEvent.HealthDegraded, published.Event);
        Assert.Equal(automation.Id, published.SourceId);
        Assert.Equal(2, published.ChainDepth); // caller depth + 1
    }

    [Theory]
    [InlineData(NotificationEvent.ReadinessFailed)]
    [InlineData(NotificationEvent.StructureDrift)]
    [InlineData(NotificationEvent.HealthDegraded)]
    public async Task EveryConfiguredEventKind_IsRaisedToDispatcherAndSink(NotificationEvent configured)
    {
        // Closes the audit gap "7 of 10 NotificationEvent types untested": the runner raises exactly the
        // configured event kinds on a non-Ok outcome, into both channels (e-mail dispatcher + event sink).
        var (runner, dispatcher, sink, store, _) = Build(
            new StubExecutor(AutomationStepType.Health, StepResult.Failed("down")));
        var automation = await store.CreateAsync(Auto() with { Events = [configured] });

        await runner.RunAsync(automation, AutomationTriggerKind.Schedule, null, 0);

        Assert.Equal(configured, Assert.Single(dispatcher.Sent).Event);
        Assert.Equal(configured, Assert.Single(sink.Published).Event);
    }

    [Fact]
    public async Task Cancellation_ReleasesClaim_WithoutCountingAFailure()
    {
        var (runner, _, _, store, _) = Build(new HangingExecutor(AutomationStepType.Health));
        var created = await store.CreateAsync(Auto());
        var claimed = DateTimeOffset.UtcNow;
        Assert.True(await store.TryBeginRunAsync(created.Id, claimed, null, TimeSpan.FromMinutes(15)));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => runner.RunAsync(created, AutomationTriggerKind.Schedule, null, 0, cts.Token));

        var reloaded = await store.GetAsync(created.Id);
        Assert.Null(reloaded!.RunningSince); // claim released
        Assert.Equal(0, reloaded.ConsecutiveFailures); // an abandoned attempt is not a failure
    }
}
