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

    private static async Task<BatchNotification> Dispatch(bool passed, int errors)
    {
        var dispatcher = new CapturingDispatcher();
        var evt = new BatchFinished(
            Guid.NewGuid(), "Alfa", "Lama",
            Total: 10, Identical: 5, Differing: 3, Errors: errors, FilesWithContentErrors: 0,
            Passed: passed, GateViolations: passed ? [] : ["differing files"], CompletedAt: DateTimeOffset.UtcNow);
        await BatchFinishedNotificationHandler.Handle(evt, dispatcher, Options.Create(new NotificationOptions()), CancellationToken.None);
        return Assert.IsType<BatchNotification>(dispatcher.Captured);
    }

    [Fact]
    public async Task Clean_pass_is_Completed()
    {
        var n = await Dispatch(passed: true, errors: 0);
        Assert.Equal(NotificationEvent.Completed, n.Event);
    }

    [Fact]
    public async Task Passed_but_with_errored_pairs_escalates_to_CompletedWithErrors()
    {
        var n = await Dispatch(passed: true, errors: 2);
        Assert.Equal(NotificationEvent.CompletedWithErrors, n.Event);
        Assert.Contains("WITH ERRORS", n.Title);
        Assert.Contains("2 error", n.Summary);
    }

    [Fact]
    public async Task Gate_violation_stays_GateViolated_even_with_errors()
    {
        var n = await Dispatch(passed: false, errors: 3);
        Assert.Equal(NotificationEvent.GateViolated, n.Event);
    }
}
