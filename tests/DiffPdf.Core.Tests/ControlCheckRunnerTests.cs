using DiffPdf.Core.Models;
using DiffPdf.Messaging.ControlPlane;
using DiffPdf.Notifications;
using DiffPdf.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiffPdf.Core.Tests;

public class ControlCheckRunnerTests
{
    private sealed class StubExecutor(CheckType type, CheckResult result) : IControlCheckExecutor
    {
        public CheckType Type => type;
        public Task<CheckResult> ExecuteAsync(ControlCheck check, CancellationToken ct) => Task.FromResult(result);
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

    private static ControlCheck Check(CheckRunOutcome? lastOutcome = null) => new()
    {
        Id = Guid.NewGuid(),
        Key = "health",
        Name = "Health",
        Type = CheckType.Health,
        IntervalSeconds = 60,
        Events = [NotificationEvent.HealthDegraded],
        LastOutcome = lastOutcome,
    };

    private static (ControlCheckRunner Runner, RecordingDispatcher Dispatcher, IControlCheckStore Checks, IControlCheckRunStore Runs)
        Build(CheckResult result)
    {
        var checks = new InMemoryControlCheckStore();
        var runs = new InMemoryControlCheckRunStore();
        var dispatcher = new RecordingDispatcher();
        var runner = new ControlCheckRunner(
            [new StubExecutor(CheckType.Health, result)],
            checks, runs, dispatcher, NullLogger<ControlCheckRunner>.Instance);
        return (runner, dispatcher, checks, runs);
    }

    [Fact]
    public async Task Failure_RecordsRun_TouchesCheck_AndNotifies()
    {
        var (runner, dispatcher, checks, runs) = Build(CheckResult.Failed("db down"));
        var check = await checks.CreateAsync(Check());

        var run = await runner.RunAsync(check);

        Assert.Equal(CheckRunOutcome.Failed, run.Outcome);
        Assert.Equal("db down", run.Detail);

        var history = await runs.ListByCheckAsync(check.Id);
        Assert.Single(history);

        var reloaded = await checks.GetAsync(check.Id);
        Assert.Equal(CheckRunOutcome.Failed, reloaded!.LastOutcome);

        var note = Assert.IsType<CheckNotification>(Assert.Single(dispatcher.Sent));
        Assert.Equal(NotificationEvent.HealthDegraded, note.Event);
    }

    [Fact]
    public async Task Success_WithNoPriorFailure_DoesNotNotify()
    {
        var (runner, dispatcher, checks, _) = Build(CheckResult.Ok("healthy"));
        var check = await checks.CreateAsync(Check());

        await runner.RunAsync(check);

        Assert.Empty(dispatcher.Sent);
    }

    [Fact]
    public async Task Success_AfterFailure_RaisesRecovered()
    {
        var (runner, dispatcher, checks, _) = Build(CheckResult.Ok("healthy"));
        // Persist the check, then run with a domain object whose LastOutcome reflects a prior failure.
        var created = await checks.CreateAsync(Check());
        var failing = created with { LastOutcome = CheckRunOutcome.Failed };

        await runner.RunAsync(failing);

        var note = Assert.IsType<CheckNotification>(Assert.Single(dispatcher.Sent));
        Assert.Equal(NotificationEvent.CheckRecovered, note.Event);
    }

    [Fact]
    public async Task UnknownType_RecordsFailedRun()
    {
        var checks = new InMemoryControlCheckStore();
        var runs = new InMemoryControlCheckRunStore();
        var dispatcher = new RecordingDispatcher();
        // No executor registered for Retention.
        var runner = new ControlCheckRunner([], checks, runs, dispatcher, NullLogger<ControlCheckRunner>.Instance);
        var check = await checks.CreateAsync(Check() with { Type = CheckType.Retention });

        var run = await runner.RunAsync(check);

        Assert.Equal(CheckRunOutcome.Failed, run.Outcome);
    }
}
