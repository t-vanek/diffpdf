using DiffPdf.Application.Abstractions;
using DiffPdf.Core.Abstractions;
using DiffPdf.Core.Models;
using DiffPdf.Messaging;
using DiffPdf.Notifications;
using DiffPdf.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DiffPdf.Core.Tests;

/// <summary>
/// The outbox delivery pass: due rows are e-mailed and marked Sent; a failure schedules a retry with the
/// 1/5/30-minute backoff; the MaxAttempts-th failure dead-letters; missing SMTP configuration counts as a
/// failed attempt (visible, not silently dropped); a manual re-queue makes a dead-lettered row due again.
/// </summary>
public class NotificationDeliveryPumpTests
{
    private sealed class RecordingSender : IEmailSender
    {
        public List<(IReadOnlyList<string> Recipients, string Subject, string Body)> Sent { get; } = [];
        public Exception? Throw { get; set; }

        public Task SendAsync(EmailSettings settings, IEnumerable<string> recipients, string subject, string body,
            IReadOnlyList<EmailAttachment>? attachments = null, CancellationToken ct = default)
        {
            if (Throw is { } ex) throw ex;
            Sent.Add((recipients.ToList(), subject, body));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeEmailSettingsStore(EmailSettings? settings) : IEmailSettingsStore
    {
        public Task<EmailSettings?> GetAsync(CancellationToken ct = default) => Task.FromResult(settings);
        public Task<EmailSettings> SaveAsync(EmailSettings s, long v, CancellationToken ct = default) => Task.FromResult(s);
    }

    private static readonly EmailSettings Configured = new() { Host = "smtp.test", FromAddress = "diffpdf@test" };

    private static NotificationDelivery Row(
        DateTimeOffset? due = null, int attemptCount = 0, NotificationDeliveryStatus status = NotificationDeliveryStatus.Pending) => new()
    {
        Id = Guid.NewGuid(),
        Event = NotificationEvent.GateViolated,
        BranchKey = "alfa",
        InstanceKey = "lama",
        RuleName = "QA",
        Recipients = ["qa@x"],
        Subject = "subj",
        Body = "body",
        Status = status,
        AttemptCount = attemptCount,
        NextAttemptAt = due ?? DateTimeOffset.UtcNow.AddSeconds(-1),
    };

    private static (NotificationDeliveryPump Pump, InMemoryNotificationDeliveryStore Store, RecordingSender Sender, DiffPdfMetrics Metrics)
        Build(EmailSettings? settings = null, int maxAttempts = 5, ISystemEventLog? systemEvents = null)
    {
        var store = new InMemoryNotificationDeliveryStore();
        var sender = new RecordingSender();
        var resolver = new EmailSettingsResolver(new FakeEmailSettingsStore(settings ?? Configured), Options.Create(new NotificationOptions()));
        var metrics = new DiffPdfMetrics();
        var pump = new NotificationDeliveryPump(store, resolver, sender, systemEvents ?? new NullSystemEventLog(), metrics,
            new NotificationDeliveryOptions { MaxAttempts = maxAttempts }, NullLogger.Instance);
        return (pump, store, sender, metrics);
    }

    [Fact]
    public async Task DueRow_IsSent_AndMarkedSent()
    {
        var (pump, store, sender, metrics) = Build();
        using var _ = metrics;
        var row = Row();
        await store.AddRangeAsync([row]);

        int processed = await pump.DeliverDueAsync(DateTimeOffset.UtcNow);

        Assert.Equal(1, processed);
        var sent = Assert.Single(sender.Sent);
        Assert.Equal(["qa@x"], sent.Recipients);
        Assert.Equal("subj", sent.Subject);
        var updated = (await store.GetAsync(row.Id))!;
        Assert.Equal(NotificationDeliveryStatus.Sent, updated.Status);
        Assert.NotNull(updated.SentAt);
        Assert.Null(updated.NextAttemptAt);
    }

    [Fact]
    public async Task FutureRow_IsNotPicked()
    {
        var (pump, store, sender, metrics) = Build();
        using var _ = metrics;
        await store.AddRangeAsync([Row(due: DateTimeOffset.UtcNow.AddMinutes(10))]);

        Assert.Equal(0, await pump.DeliverDueAsync(DateTimeOffset.UtcNow));
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task FirstFailure_SchedulesRetry_InAboutAMinute()
    {
        var (pump, store, sender, metrics) = Build();
        using var _ = metrics;
        sender.Throw = new InvalidOperationException("smtp down");
        var row = Row();
        await store.AddRangeAsync([row]);

        await pump.DeliverDueAsync(DateTimeOffset.UtcNow);

        var updated = (await store.GetAsync(row.Id))!;
        Assert.Equal(NotificationDeliveryStatus.Failed, updated.Status);
        Assert.Equal(1, updated.AttemptCount);
        Assert.Equal("smtp down", updated.LastError);
        var delay = updated.NextAttemptAt!.Value - DateTimeOffset.UtcNow;
        Assert.InRange(delay.TotalSeconds, 30, 90); // ~1 min backoff
    }

    [Theory]
    [InlineData(1, 5)]   // second attempt failing → +5 min
    [InlineData(2, 30)]  // third attempt failing → +30 min
    [InlineData(3, 30)]  // later attempts stay at 30 min
    public async Task LaterFailures_BackOffLonger(int priorAttempts, int expectedMinutes)
    {
        var (pump, store, sender, metrics) = Build();
        using var _ = metrics;
        sender.Throw = new InvalidOperationException("smtp down");
        var row = Row(attemptCount: priorAttempts, status: NotificationDeliveryStatus.Failed);
        await store.AddRangeAsync([row]);

        await pump.DeliverDueAsync(DateTimeOffset.UtcNow);

        var updated = (await store.GetAsync(row.Id))!;
        Assert.Equal(priorAttempts + 1, updated.AttemptCount);
        var delay = updated.NextAttemptAt!.Value - DateTimeOffset.UtcNow;
        Assert.InRange(delay.TotalMinutes, expectedMinutes - 1, expectedMinutes + 1);
    }

    [Fact]
    public async Task MaxAttemptsReached_DeadLetters_AndStopsRetrying()
    {
        var (pump, store, sender, metrics) = Build(maxAttempts: 3);
        using var _ = metrics;
        sender.Throw = new InvalidOperationException("smtp down");
        var row = Row(attemptCount: 2, status: NotificationDeliveryStatus.Failed); // the 3rd attempt is the last
        await store.AddRangeAsync([row]);

        await pump.DeliverDueAsync(DateTimeOffset.UtcNow);

        var updated = (await store.GetAsync(row.Id))!;
        Assert.Equal(NotificationDeliveryStatus.DeadLetter, updated.Status);
        Assert.Equal(3, updated.AttemptCount);
        Assert.Null(updated.NextAttemptAt);

        // Dead-lettered rows are never picked again.
        sender.Throw = null;
        Assert.Equal(0, await pump.DeliverDueAsync(DateTimeOffset.UtcNow.AddHours(2)));
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task MissingSmtpConfiguration_CountsAsFailedAttempt_NotSilentDrop()
    {
        var (pump, store, _, metrics) = Build(settings: new EmailSettings()); // no Host → not configured
        using var _ = metrics;
        var row = Row();
        await store.AddRangeAsync([row]);

        await pump.DeliverDueAsync(DateTimeOffset.UtcNow);

        var updated = (await store.GetAsync(row.Id))!;
        Assert.Equal(NotificationDeliveryStatus.Failed, updated.Status);
        Assert.Contains("nakonfigurován", updated.LastError);
    }

    [Fact]
    public async Task Requeue_MakesDeadLetterDueAgain_AndItSends()
    {
        var (pump, store, sender, metrics) = Build(maxAttempts: 1);
        using var _ = metrics;
        sender.Throw = new InvalidOperationException("smtp down");
        var row = Row();
        await store.AddRangeAsync([row]);
        await pump.DeliverDueAsync(DateTimeOffset.UtcNow);
        Assert.Equal(NotificationDeliveryStatus.DeadLetter, (await store.GetAsync(row.Id))!.Status);

        // The operator fixed SMTP and clicked "Poslat znovu".
        sender.Throw = null;
        Assert.True(await store.RequeueAsync(row.Id, DateTimeOffset.UtcNow));
        await pump.DeliverDueAsync(DateTimeOffset.UtcNow);

        Assert.Single(sender.Sent);
        Assert.Equal(NotificationDeliveryStatus.Sent, (await store.GetAsync(row.Id))!.Status);
    }

    [Fact]
    public async Task DeadLetter_AppendsErrorSystemEvent()
    {
        // A dead-lettered alert surfaces in the event feed — the e-mail channel just proved broken,
        // so the operator must learn about it through the other channel.
        var eventStore = new InMemorySystemEventStore();
        var log = new SystemEventLog(eventStore, new NullSystemEventPublisher(), NullLogger<SystemEventLog>.Instance);
        var (pump, store, sender, metrics) = Build(maxAttempts: 1, systemEvents: log);
        using var _ = metrics;
        sender.Throw = new InvalidOperationException("smtp down");
        await store.AddRangeAsync([Row()]);

        await pump.DeliverDueAsync(DateTimeOffset.UtcNow);

        var evt = Assert.Single(await eventStore.ListSinceAsync(0, 10));
        Assert.Equal(SystemEventTypes.NotificationDeadLetter, evt.Type);
        Assert.Equal(SystemEventSeverity.Error, evt.Severity);
        Assert.Contains("nepodařilo doručit", evt.Message);
    }

    [Fact]
    public async Task SentRows_AreNeverResent()
    {
        var (pump, store, sender, metrics) = Build();
        using var _ = metrics;
        var row = Row();
        await store.AddRangeAsync([row]);
        await pump.DeliverDueAsync(DateTimeOffset.UtcNow);
        Assert.Single(sender.Sent);

        await pump.DeliverDueAsync(DateTimeOffset.UtcNow.AddMinutes(5));
        Assert.Single(sender.Sent); // still exactly one
    }
}
