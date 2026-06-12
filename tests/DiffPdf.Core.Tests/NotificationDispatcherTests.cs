using DiffPdf.Core.Models;
using DiffPdf.Notifications;
using DiffPdf.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiffPdf.Core.Tests;

/// <summary>
/// The dispatcher is an outbox appender: it matches enabled rules (event + scope) and queues one
/// <see cref="NotificationDelivery"/> row per match — it never sends inline (the delivery service does,
/// with retries). These tests pin the matching semantics and the queued row's shape.
/// </summary>
public class NotificationDispatcherTests
{
    private static BatchNotification Notification(
        NotificationEvent ev = NotificationEvent.Completed, string branch = "alfa", string instance = "lama") =>
        new(ev, Guid.NewGuid(), branch, instance, 10, 7, 3, 0, 0,
            Passed: true, GateViolations: [], DateTimeOffset.UtcNow);

    private static NotificationSubscription Rule(
        IReadOnlyList<string> recipients, NotificationEvent[] events,
        IReadOnlyList<string>? branches = null, IReadOnlyList<string>? instances = null, bool enabled = true,
        string name = "rule") =>
        new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            Recipients = recipients,
            Events = events,
            BranchKeys = branches ?? [],
            InstanceKeys = instances ?? [],
            Enabled = enabled,
        };

    private static async Task<(NotificationDispatcher Dispatcher, InMemoryNotificationDeliveryStore Outbox)> DispatcherAsync(
        IEnumerable<NotificationSubscription> rules)
    {
        var store = new InMemorySubscriptionStore();
        foreach (var r in rules) await store.CreateAsync(r);
        var outbox = new InMemoryNotificationDeliveryStore();
        return (new NotificationDispatcher(store, outbox, NullLogger<NotificationDispatcher>.Instance), outbox);
    }

    private static Task<IReadOnlyList<NotificationDelivery>> RowsAsync(InMemoryNotificationDeliveryStore outbox) =>
        outbox.ListRecentAsync(100);

    [Fact]
    public async Task DisabledRule_QueuesNothing()
    {
        var (dispatcher, outbox) = await DispatcherAsync([Rule(["qa@x"], [NotificationEvent.Completed], enabled: false)]);
        await dispatcher.DispatchAsync(Notification());
        Assert.Empty(await RowsAsync(outbox));
    }

    [Fact]
    public async Task NoRules_QueuesNothing()
    {
        var (dispatcher, outbox) = await DispatcherAsync([]);
        await dispatcher.DispatchAsync(Notification());
        Assert.Empty(await RowsAsync(outbox));
    }

    [Fact]
    public async Task FiltersByEvent()
    {
        var (dispatcher, outbox) = await DispatcherAsync([Rule(["qa@x"], [NotificationEvent.Completed])]);
        await dispatcher.DispatchAsync(Notification(NotificationEvent.Failed));
        Assert.Empty(await RowsAsync(outbox));
    }

    [Fact]
    public async Task FiltersByBranch()
    {
        var (dispatcher, outbox) = await DispatcherAsync([Rule(["qa@x"], [NotificationEvent.Completed], branches: ["beta"])]);
        await dispatcher.DispatchAsync(Notification(branch: "alfa"));
        Assert.Empty(await RowsAsync(outbox));
    }

    [Fact]
    public async Task FiltersByInstance()
    {
        var (dispatcher, outbox) = await DispatcherAsync([Rule(["qa@x"], [NotificationEvent.Completed], instances: ["other"])]);
        await dispatcher.DispatchAsync(Notification(instance: "lama"));
        Assert.Empty(await RowsAsync(outbox));
    }

    [Fact]
    public async Task MatchingRule_QueuesOneRowWithSnapshot()
    {
        var rule = Rule(["qa@x", "dev@x"], [NotificationEvent.Completed], branches: ["alfa"], instances: ["lama"], name: "QA tým");
        var (dispatcher, outbox) = await DispatcherAsync([rule]);

        await dispatcher.DispatchAsync(Notification(branch: "alfa", instance: "lama"));

        var row = Assert.Single(await RowsAsync(outbox));
        Assert.Equal(NotificationDeliveryStatus.Pending, row.Status);
        Assert.Equal(["qa@x", "dev@x"], row.Recipients);
        Assert.Equal(rule.Id, row.SubscriptionId);
        Assert.Equal("QA tým", row.RuleName);
        Assert.Equal(NotificationEvent.Completed, row.Event);
        Assert.Equal("alfa", row.BranchKey);
        Assert.Equal("lama", row.InstanceKey);
        Assert.NotNull(row.NextAttemptAt);            // due immediately
        Assert.False(string.IsNullOrWhiteSpace(row.Subject));
        Assert.False(string.IsNullOrWhiteSpace(row.Body));
    }

    [Fact]
    public async Task EmptyScope_MatchesAnyBranchAndInstance()
    {
        var (dispatcher, outbox) = await DispatcherAsync([Rule(["qa@x"], [NotificationEvent.Completed])]);
        await dispatcher.DispatchAsync(Notification(branch: "whatever", instance: "any"));
        Assert.Single(await RowsAsync(outbox));
    }

    [Fact]
    public async Task TwoMatchingRules_QueueTwoRows()
    {
        var (dispatcher, outbox) = await DispatcherAsync(
        [
            Rule(["qa@x"], [NotificationEvent.Completed]),
            Rule(["dev@x"], [NotificationEvent.Completed]),
        ]);

        await dispatcher.DispatchAsync(Notification());

        Assert.Equal(2, (await RowsAsync(outbox)).Count);
    }

    [Fact]
    public async Task QueuesEvenWhenSmtpIsNotConfigured()
    {
        // The old inline dispatcher silently dropped notifications when SMTP was not configured. The outbox
        // queues them regardless — the delivery service fails the attempts and parks the rows as DeadLetter,
        // which makes the misconfiguration visible (and the rows re-sendable once SMTP is fixed).
        var (dispatcher, outbox) = await DispatcherAsync([Rule(["qa@x"], [NotificationEvent.Completed])]);
        await dispatcher.DispatchAsync(Notification());
        Assert.Single(await RowsAsync(outbox));
    }
}
