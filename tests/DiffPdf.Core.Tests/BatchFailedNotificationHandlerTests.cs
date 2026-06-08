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

    [Fact]
    public async Task MapsBatchFailed_ToFailedNotification_WithDeepLink()
    {
        var dispatcher = new CapturingDispatcher();
        var options = Options.Create(new NotificationOptions { BaseUrl = "https://diffpdf.example/" });
        var evt = new BatchFailed(Guid.NewGuid(), "Alfa", "Lama", "indexing blew up", DateTimeOffset.UtcNow);

        await BatchFailedNotificationHandler.Handle(evt, dispatcher, options, CancellationToken.None);

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

        await BatchFailedNotificationHandler.Handle(evt, dispatcher, options, CancellationToken.None);

        var captured = Assert.IsType<BatchNotification>(dispatcher.Captured);
        Assert.Null(captured.Link);
        Assert.DoesNotContain("Details:", captured.Summary);
    }
}
