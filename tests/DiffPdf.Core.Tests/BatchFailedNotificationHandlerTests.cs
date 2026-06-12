using DiffPdf.Application.Abstractions;
using DiffPdf.Core.Models;
using DiffPdf.Messaging.Handlers;
using DiffPdf.Messaging.Messages;
using DiffPdf.Notifications;
using Microsoft.Extensions.Options;

namespace DiffPdf.Core.Tests;

public class BatchFailedNotificationHandlerTests
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

    [Fact]
    public async Task MapsBatchFailed_ToFailedNotification_WithDeepLink()
    {
        var dispatcher = new CapturingDispatcher();
        var options = Options.Create(new NotificationOptions { BaseUrl = "https://diffpdf.example/" });
        var evt = new BatchFailed(Guid.NewGuid(), "Alfa", "Lama", "indexing blew up", DateTimeOffset.UtcNow);

        await BatchFailedNotificationHandler.Handle(evt, dispatcher, new CapturingSink(), options, CancellationToken.None);

        var captured = Assert.IsType<BatchNotification>(dispatcher.Captured);
        Assert.Equal(NotificationEvent.Failed, captured.Event);
        Assert.Equal(evt.JobId, captured.JobId);
        Assert.Equal("Alfa", captured.BranchKey);
        Assert.Equal("Lama", captured.InstanceKey);
        Assert.False(captured.Passed);
        // BaseUrl is configured -> a deep link to the job is built (trailing slash trimmed) and rendered in the body.
        Assert.Equal($"https://diffpdf.example/api/v1/jobs/{evt.JobId}", captured.Link);
        Assert.Contains(captured.Link!, captured.Summary);
    }

    [Fact]
    public async Task NoBaseUrl_LeavesLinkNull_AndOutOfTheBody()
    {
        var dispatcher = new CapturingDispatcher();
        var options = Options.Create(new NotificationOptions());
        var evt = new BatchFailed(Guid.NewGuid(), "Alfa", "Lama", "boom", DateTimeOffset.UtcNow);

        await BatchFailedNotificationHandler.Handle(evt, dispatcher, new CapturingSink(), options, CancellationToken.None);

        var captured = Assert.IsType<BatchNotification>(dispatcher.Captured);
        Assert.Null(captured.Link);
        Assert.DoesNotContain("Details:", captured.Summary);
    }

    [Fact]
    public async Task Failure_fires_event_triggered_automations_at_chain_root()
    {
        var sink = new CapturingSink();
        var evt = new BatchFailed(Guid.NewGuid(), "Alfa", "Lama", "boom", DateTimeOffset.UtcNow);

        await BatchFailedNotificationHandler.Handle(evt, new CapturingDispatcher(), sink, Options.Create(new NotificationOptions()), CancellationToken.None);

        var published = Assert.Single(sink.Published);
        Assert.Equal(NotificationEvent.Failed, published.Event);
        Assert.Equal("Alfa", published.BranchKey);
        Assert.Equal("Lama", published.InstanceKey);
        Assert.Null(published.SourceAutomationId);
        Assert.Equal(0, published.ChainDepth); // ad-hoc batch starts a fresh cascade
    }

    [Fact]
    public async Task Automation_launched_batch_excludes_its_launcher_and_counts_one_hop()
    {
        var sink = new CapturingSink();
        var launcher = Guid.NewGuid();
        var evt = new BatchFailed(Guid.NewGuid(), "Alfa", "Lama", "boom", DateTimeOffset.UtcNow, SourceAutomationId: launcher);

        await BatchFailedNotificationHandler.Handle(evt, new CapturingDispatcher(), sink, Options.Create(new NotificationOptions()), CancellationToken.None);

        var published = Assert.Single(sink.Published);
        Assert.Equal(launcher, published.SourceAutomationId); // sink excludes this automation from triggering
        Assert.Equal(1, published.ChainDepth);
    }
}
