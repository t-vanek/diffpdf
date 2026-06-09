using DiffPdf.Core.Models;
using DiffPdf.Notifications;
using DiffPdf.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DiffPdf.Core.Tests;

public class NotificationDispatcherTests
{
    private sealed class RecordingSender : IEmailSender
    {
        public List<(IReadOnlyList<string> Recipients, string Subject)> Sent { get; } = [];
        public Func<IReadOnlyList<string>, bool>? ThrowFor { get; init; }

        public Task SendAsync(EmailSettings settings, IEnumerable<string> recipients, string subject, string body, CancellationToken ct = default)
        {
            var list = recipients.ToList();
            if (ThrowFor is not null && ThrowFor(list)) throw new InvalidOperationException("boom");
            Sent.Add((list, subject));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeEmailSettingsStore(EmailSettings? settings) : IEmailSettingsStore
    {
        public Task<EmailSettings?> GetAsync(CancellationToken ct = default) => Task.FromResult(settings);
        public Task<EmailSettings> SaveAsync(EmailSettings s, long v, CancellationToken ct = default) => Task.FromResult(s);
    }

    private static readonly EmailSettings Configured = new() { Host = "smtp.test", FromAddress = "diffpdf@test" };

    private static BatchNotification Notification(
        NotificationEvent ev = NotificationEvent.Completed, string branch = "alfa", string instance = "lama") =>
        new(ev, Guid.NewGuid(), branch, instance, 10, 7, 3, 0, 0,
            Passed: true, GateViolations: [], DateTimeOffset.UtcNow);

    private static NotificationSubscription Rule(
        IReadOnlyList<string> recipients, NotificationEvent[] events,
        IReadOnlyList<string>? branches = null, IReadOnlyList<string>? instances = null, bool enabled = true) =>
        new()
        {
            Id = Guid.NewGuid(),
            Recipients = recipients,
            Events = events,
            BranchKeys = branches ?? [],
            InstanceKeys = instances ?? [],
            Enabled = enabled,
        };

    private static async Task<NotificationDispatcher> DispatcherAsync(
        IEnumerable<NotificationSubscription> rules, IEmailSender sender, EmailSettings? settings = null)
    {
        var store = new InMemorySubscriptionStore();
        foreach (var r in rules) await store.CreateAsync(r);
        var resolver = new EmailSettingsResolver(new FakeEmailSettingsStore(settings ?? Configured), Options.Create(new NotificationOptions()));
        return new NotificationDispatcher(store, resolver, sender, NullLogger<NotificationDispatcher>.Instance);
    }

    [Fact]
    public async Task DisabledRule_DispatchesNothing()
    {
        var sender = new RecordingSender();
        var rule = Rule(["qa@x"], [NotificationEvent.Completed], enabled: false);

        await (await DispatcherAsync([rule], sender)).DispatchAsync(Notification());

        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task NoRules_DispatchesNothing()
    {
        var sender = new RecordingSender();
        await (await DispatcherAsync([], sender)).DispatchAsync(Notification());
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task FiltersByEvent()
    {
        var sender = new RecordingSender();
        var rule = Rule(["qa@x"], [NotificationEvent.Completed]);

        await (await DispatcherAsync([rule], sender)).DispatchAsync(Notification(NotificationEvent.Failed));

        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task FiltersByBranch()
    {
        var sender = new RecordingSender();
        var rule = Rule(["qa@x"], [NotificationEvent.Completed], branches: ["beta"]);

        await (await DispatcherAsync([rule], sender)).DispatchAsync(Notification(branch: "alfa"));

        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task FiltersByInstance()
    {
        var sender = new RecordingSender();
        var rule = Rule(["qa@x"], [NotificationEvent.Completed], instances: ["other"]);

        await (await DispatcherAsync([rule], sender)).DispatchAsync(Notification(instance: "lama"));

        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task MatchingRule_SendsToAllRecipients()
    {
        var sender = new RecordingSender();
        var rule = Rule(["qa@x", "dev@x"], [NotificationEvent.Completed], branches: ["alfa"], instances: ["lama"]);

        await (await DispatcherAsync([rule], sender)).DispatchAsync(Notification(branch: "alfa", instance: "lama"));

        Assert.Single(sender.Sent);
        Assert.Equal(["qa@x", "dev@x"], sender.Sent[0].Recipients);
    }

    [Fact]
    public async Task EmptyScope_MatchesAnyBranchAndInstance()
    {
        var sender = new RecordingSender();
        var rule = Rule(["qa@x"], [NotificationEvent.Completed]); // no branch/instance filter

        await (await DispatcherAsync([rule], sender)).DispatchAsync(Notification(branch: "whatever", instance: "any"));

        Assert.Single(sender.Sent);
    }

    [Fact]
    public async Task NotConfigured_DispatchesNothing()
    {
        var sender = new RecordingSender();
        var rule = Rule(["qa@x"], [NotificationEvent.Completed]);

        // No stored settings and an empty NotificationOptions => resolver returns not-configured.
        await (await DispatcherAsync([rule], sender, settings: new EmailSettings())).DispatchAsync(Notification());

        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task OneFailingRule_DoesNotBlockOthers()
    {
        var sender = new RecordingSender { ThrowFor = r => r.Contains("boom@x") };
        var rules = new[]
        {
            Rule(["boom@x"], [NotificationEvent.Completed]),
            Rule(["ok@x"], [NotificationEvent.Completed]),
        };

        await (await DispatcherAsync(rules, sender)).DispatchAsync(Notification());

        Assert.Single(sender.Sent); // the failing rule is logged; the other still delivers
        Assert.Equal(["ok@x"], sender.Sent[0].Recipients);
    }
}
