using DiffPdf.Core.Models;
using DiffPdf.Messaging.Handlers;
using DiffPdf.Messaging.Messages;
using DiffPdf.Notifications;

namespace DiffPdf.Core.Tests;

public class BatchFailedNotificationHandlerTests
{
    private sealed class CapturingDispatcher : INotificationDispatcher
    {
        public BatchNotification? Captured;

        public Task DispatchAsync(BatchNotification notification, CancellationToken ct = default)
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

        Assert.NotNull(dispatcher.Captured);
        Assert.Equal(NotificationEvent.Failed, dispatcher.Captured!.Event);
        Assert.Equal(evt.JobId, dispatcher.Captured.JobId);
        Assert.Equal("Alfa", dispatcher.Captured.BranchKey);
        Assert.Equal("Lama", dispatcher.Captured.InstanceKey);
        Assert.False(dispatcher.Captured.Passed);
    }
}
