using DiffPdf.Application.Abstractions;
using DiffPdf.Core.Models;
using DiffPdf.Messaging.Handlers;
using DiffPdf.Messaging.Messages;
using DiffPdf.Notifications;
using Microsoft.Extensions.Options;

namespace DiffPdf.Core.Tests;

public class BatchFinishedNotificationHandlerTests
{
    private sealed class CapturingDispatcher : INotificationDispatcher
    {
        public INotification? Captured;

        public Task DispatchAsync(INotification notification, CancellationToken ct = default)
        {
            Captured = notification;
            return Task.CompletedTask;
        }
    }

    private sealed class CapturingSink : IAutomationEventSink
    {
        public readonly List<(NotificationEvent Event, string? BranchKey, string? InstanceKey, Guid? SourceAutomationId, int ChainDepth)> Published = [];

        public Task PublishAsync(NotificationEvent @event, string? branchKey, string? instanceKey, string detail, Guid? sourceAutomationId, int chainDepth, CancellationToken ct = default)
        {
            Published.Add((@event, branchKey, instanceKey, sourceAutomationId, chainDepth));
            return Task.CompletedTask;
        }
    }

    private static async Task<(BatchNotification Notification, CapturingSink Sink)> Dispatch(
        bool passed, int errors, Guid? sourceAutomationId = null)
    {
        var dispatcher = new CapturingDispatcher();
        var sink = new CapturingSink();
        var evt = new BatchFinished(
            Guid.NewGuid(), "Alfa", "Lama",
            Total: 10, Identical: 5, Differing: 3, Errors: errors, FilesWithContentErrors: 0,
            Passed: passed, GateViolations: passed ? [] : ["differing files"], CompletedAt: DateTimeOffset.UtcNow,
            SourceAutomationId: sourceAutomationId);
        await BatchFinishedNotificationHandler.Handle(evt, dispatcher, sink, Options.Create(new NotificationOptions()), CancellationToken.None);
        return (Assert.IsType<BatchNotification>(dispatcher.Captured), sink);
    }

    [Fact]
    public async Task Clean_pass_is_Completed()
    {
        var (n, _) = await Dispatch(passed: true, errors: 0);
        Assert.Equal(NotificationEvent.Completed, n.Event);
    }

    [Fact]
    public async Task Passed_but_with_errored_pairs_escalates_to_CompletedWithErrors()
    {
        var (n, _) = await Dispatch(passed: true, errors: 2);
        Assert.Equal(NotificationEvent.CompletedWithErrors, n.Event);
        Assert.Contains("WITH ERRORS", n.Title);
        Assert.Contains("2 error", n.Summary);
    }

    [Fact]
    public async Task Gate_violation_stays_GateViolated_even_with_errors()
    {
        var (n, _) = await Dispatch(passed: false, errors: 3);
        Assert.Equal(NotificationEvent.GateViolated, n.Event);
    }

    [Fact]
    public async Task Outcome_fires_event_triggered_automations_with_the_same_kind()
    {
        var (_, sink) = await Dispatch(passed: false, errors: 0);
        var published = Assert.Single(sink.Published);
        Assert.Equal(NotificationEvent.GateViolated, published.Event);
        Assert.Equal("Alfa", published.BranchKey);
        Assert.Equal("Lama", published.InstanceKey);
        Assert.Null(published.SourceAutomationId);
        Assert.Equal(0, published.ChainDepth);
    }

    [Fact]
    public async Task Automation_launched_batch_excludes_its_launcher_and_counts_one_hop()
    {
        var launcher = Guid.NewGuid();
        var (_, sink) = await Dispatch(passed: true, errors: 0, sourceAutomationId: launcher);
        var published = Assert.Single(sink.Published);
        Assert.Equal(NotificationEvent.Completed, published.Event);
        Assert.Equal(launcher, published.SourceAutomationId);
        Assert.Equal(1, published.ChainDepth);
    }
}
