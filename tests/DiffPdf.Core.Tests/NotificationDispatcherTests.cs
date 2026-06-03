using DiffPdf.Notifications;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DiffPdf.Core.Tests;

public class NotificationDispatcherTests
{
    private sealed class RecordingNotifier(string channel, bool throwOnSend = false) : INotifier
    {
        public string Channel { get; } = channel;
        public List<BatchNotification> Sent { get; } = [];

        public Task SendAsync(NotificationSubscription subscription, BatchNotification notification, CancellationToken ct)
        {
            if (throwOnSend)
                throw new InvalidOperationException("boom");
            Sent.Add(notification);
            return Task.CompletedTask;
        }
    }

    private static BatchNotification Notification(
        NotificationEvent ev = NotificationEvent.GateViolated,
        string branch = "Alfa", string instance = "Lama") =>
        new(ev, Guid.NewGuid(), branch, instance, 10, 7, 3, 0, 0,
            Passed: ev == NotificationEvent.Completed, GateViolations: ["differing files"], DateTimeOffset.UtcNow);

    private static NotificationDispatcher Dispatcher(NotificationOptions opts, params INotifier[] notifiers) =>
        new(notifiers, Options.Create(opts), NullLogger<NotificationDispatcher>.Instance);

    [Fact]
    public async Task Disabled_DispatchesNothing()
    {
        var webhook = new RecordingNotifier("webhook");
        var opts = new NotificationOptions
        {
            Enabled = false,
            Subscriptions = [new() { Channel = "webhook", Target = "http://x", Events = [NotificationEvent.GateViolated] }],
        };

        await Dispatcher(opts, webhook).DispatchAsync(Notification());

        Assert.Empty(webhook.Sent);
    }

    [Fact]
    public async Task FiltersByEvent()
    {
        var webhook = new RecordingNotifier("webhook");
        var opts = new NotificationOptions
        {
            Enabled = true,
            Subscriptions = [new() { Channel = "webhook", Target = "http://x", Events = [NotificationEvent.Completed] }],
        };

        await Dispatcher(opts, webhook).DispatchAsync(Notification(NotificationEvent.GateViolated));

        Assert.Empty(webhook.Sent);
    }

    [Fact]
    public async Task FiltersByBranchAndInstance()
    {
        var webhook = new RecordingNotifier("webhook");
        var opts = new NotificationOptions
        {
            Enabled = true,
            Subscriptions =
            [
                new() { Channel = "webhook", Target = "http://x", Events = [NotificationEvent.GateViolated], BranchKey = "Beta" },
            ],
        };

        await Dispatcher(opts, webhook).DispatchAsync(Notification(branch: "Alfa"));

        Assert.Empty(webhook.Sent);
    }

    [Fact]
    public async Task RoutesToMatchingChannel_CaseInsensitive()
    {
        var webhook = new RecordingNotifier("webhook");
        var smtp = new RecordingNotifier("smtp");
        var opts = new NotificationOptions
        {
            Enabled = true,
            Subscriptions = [new() { Channel = "WEBHOOK", Target = "http://x", Events = [NotificationEvent.GateViolated] }],
        };

        await Dispatcher(opts, webhook, smtp).DispatchAsync(Notification());

        Assert.Single(webhook.Sent);
        Assert.Empty(smtp.Sent);
    }

    [Fact]
    public async Task OneFailingSubscription_DoesNotBlockOthers()
    {
        var failing = new RecordingNotifier("webhook", throwOnSend: true);
        var smtp = new RecordingNotifier("smtp");
        var opts = new NotificationOptions
        {
            Enabled = true,
            Subscriptions =
            [
                new() { Channel = "webhook", Target = "http://x", Events = [NotificationEvent.GateViolated] },
                new() { Channel = "smtp", Target = "qa@x", Events = [NotificationEvent.GateViolated] },
            ],
        };

        await Dispatcher(opts, failing, smtp).DispatchAsync(Notification());

        Assert.Single(smtp.Sent); // smtp still delivered despite webhook throwing
    }
}
