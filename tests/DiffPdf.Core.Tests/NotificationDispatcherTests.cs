using DiffPdf.Core.Models;
using DiffPdf.Notifications;
using DiffPdf.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

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

    private static NotificationSubscription Sub(
        string channel, string target, NotificationEvent[] events,
        string? branch = null, string? instance = null, bool enabled = true) =>
        new()
        {
            Id = Guid.NewGuid(),
            Channel = channel,
            Target = target,
            Events = events,
            BranchKey = branch,
            InstanceKey = instance,
            Enabled = enabled,
        };

    private static async Task<NotificationDispatcher> DispatcherAsync(
        IEnumerable<NotificationSubscription> subscriptions, params INotifier[] notifiers)
    {
        var store = new InMemorySubscriptionStore();
        foreach (var s in subscriptions)
            await store.CreateAsync(s);
        return new NotificationDispatcher(notifiers, store, NullLogger<NotificationDispatcher>.Instance);
    }

    [Fact]
    public async Task DisabledSubscription_DispatchesNothing()
    {
        var webhook = new RecordingNotifier("webhook");
        var sub = Sub("webhook", "http://x", [NotificationEvent.GateViolated], enabled: false);

        await (await DispatcherAsync([sub], webhook)).DispatchAsync(Notification());

        Assert.Empty(webhook.Sent);
    }

    [Fact]
    public async Task NoSubscriptions_DispatchesNothing()
    {
        var webhook = new RecordingNotifier("webhook");

        await (await DispatcherAsync([], webhook)).DispatchAsync(Notification());

        Assert.Empty(webhook.Sent);
    }

    [Fact]
    public async Task FiltersByEvent()
    {
        var webhook = new RecordingNotifier("webhook");
        var sub = Sub("webhook", "http://x", [NotificationEvent.Completed]);

        await (await DispatcherAsync([sub], webhook)).DispatchAsync(Notification(NotificationEvent.GateViolated));

        Assert.Empty(webhook.Sent);
    }

    [Fact]
    public async Task FiltersByBranchAndInstance()
    {
        var webhook = new RecordingNotifier("webhook");
        var sub = Sub("webhook", "http://x", [NotificationEvent.GateViolated], branch: "Beta");

        await (await DispatcherAsync([sub], webhook)).DispatchAsync(Notification(branch: "Alfa"));

        Assert.Empty(webhook.Sent);
    }

    [Fact]
    public async Task RoutesToMatchingChannel_CaseInsensitive()
    {
        var webhook = new RecordingNotifier("webhook");
        var smtp = new RecordingNotifier("smtp");
        var sub = Sub("WEBHOOK", "http://x", [NotificationEvent.GateViolated]);

        await (await DispatcherAsync([sub], webhook, smtp)).DispatchAsync(Notification());

        Assert.Single(webhook.Sent);
        Assert.Empty(smtp.Sent);
    }

    [Fact]
    public async Task OneFailingSubscription_DoesNotBlockOthers()
    {
        var failing = new RecordingNotifier("webhook", throwOnSend: true);
        var smtp = new RecordingNotifier("smtp");
        var subs = new[]
        {
            Sub("webhook", "http://x", [NotificationEvent.GateViolated]),
            Sub("smtp", "qa@x", [NotificationEvent.GateViolated]),
        };

        await (await DispatcherAsync(subs, failing, smtp)).DispatchAsync(Notification());

        Assert.Single(smtp.Sent); // smtp still delivered despite webhook throwing
    }
}
