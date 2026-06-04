using DiffPdf.Core.Models;
using DiffPdf.Messaging.Handlers;
using DiffPdf.Messaging.Messages;
using DiffPdf.Notifications;

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
    public async Task MapsBatchFailed_ToFailedNotification()
    {
        var dispatcher = new CapturingDispatcher();
        var evt = new BatchFailed(Guid.NewGuid(), "Alfa", "Lama", "indexing blew up", DateTimeOffset.UtcNow);

        await BatchFailedNotificationHandler.Handle(evt, dispatcher, CancellationToken.None);

        var captured = Assert.IsType<BatchNotification>(dispatcher.Captured);
        Assert.Equal(NotificationEvent.Failed, captured.Event);
        Assert.Equal(evt.JobId, captured.JobId);
        Assert.Equal("Alfa", captured.BranchKey);
        Assert.Equal("Lama", captured.InstanceKey);
        Assert.False(captured.Passed);
    }
}
