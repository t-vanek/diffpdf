using DiffPdf.Core.Abstractions;
using DiffPdf.Core.Models;
using DiffPdf.Messaging;
using DiffPdf.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiffPdf.Core.Tests;

/// <summary>
/// The system event log: appends assign a monotonic Seq cursor, the realtime push carries the persisted
/// Seq, and the whole append is best-effort — a store or publisher failure never propagates to the domain
/// flow that raised the event.
/// </summary>
public class SystemEventLogTests
{
    private sealed class CapturingPublisher : ISystemEventPublisher
    {
        public List<SystemEvent> Published { get; } = [];
        public Task PublishAsync(SystemEvent evt, CancellationToken ct = default)
        {
            Published.Add(evt);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingPublisher : ISystemEventPublisher
    {
        public Task PublishAsync(SystemEvent evt, CancellationToken ct = default) =>
            throw new InvalidOperationException("signalr down");
    }

    private sealed class ThrowingStore : ISystemEventStore
    {
        public Task<SystemEvent> AppendAsync(SystemEvent evt, CancellationToken ct = default) =>
            throw new InvalidOperationException("db down");
        public Task<IReadOnlyList<SystemEvent>> ListSinceAsync(long sinceSeq, int limit, SystemEventSeverity? minSeverity = null, CancellationToken ct = default) =>
            throw new NotImplementedException();
        public Task<IReadOnlyList<SystemEvent>> ListRecentAsync(int limit, CancellationToken ct = default) =>
            throw new NotImplementedException();
        public Task<bool> ExistsForJobAsync(string type, Guid jobId, DateTimeOffset after, CancellationToken ct = default) =>
            throw new NotImplementedException();
        public Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default) =>
            throw new NotImplementedException();
    }

    private static SystemEvent Event(string type = SystemEventTypes.JobCompleted, string message = "hotovo") =>
        new() { Type = type, Message = message };

    [Fact]
    public async Task Append_AssignsMonotonicSeq_AndPushesThePersistedEvent()
    {
        var store = new InMemorySystemEventStore();
        var publisher = new CapturingPublisher();
        var log = new SystemEventLog(store, publisher, NullLogger<SystemEventLog>.Instance);

        await log.AppendAsync(Event(message: "první"));
        await log.AppendAsync(Event(message: "druhá"));

        var rows = await store.ListSinceAsync(0, 10);
        Assert.Equal(2, rows.Count);
        Assert.True(rows[1].Seq > rows[0].Seq);
        // The push carries the assigned Seq, so a client can replay from exactly where it left off.
        Assert.Equal(rows.Select(r => r.Seq), publisher.Published.Select(p => p.Seq));
    }

    [Fact]
    public async Task StoreFailure_IsSwallowed_AndNothingIsPushed()
    {
        var publisher = new CapturingPublisher();
        var log = new SystemEventLog(new ThrowingStore(), publisher, NullLogger<SystemEventLog>.Instance);

        await log.AppendAsync(Event()); // must not throw

        Assert.Empty(publisher.Published); // no Seq → no push (clients could never replay it)
    }

    [Fact]
    public async Task PublisherFailure_IsSwallowed_AndTheEventStaysPersisted()
    {
        var store = new InMemorySystemEventStore();
        var log = new SystemEventLog(store, new ThrowingPublisher(), NullLogger<SystemEventLog>.Instance);

        await log.AppendAsync(Event()); // must not throw

        Assert.Single(await store.ListSinceAsync(0, 10)); // replayable via the REST cursor
    }

    [Fact]
    public async Task ListSince_IsAnExclusiveCursor_WithSeverityFloor()
    {
        var store = new InMemorySystemEventStore();
        var info = await store.AppendAsync(Event() with { Severity = SystemEventSeverity.Info });
        var warn = await store.AppendAsync(Event() with { Severity = SystemEventSeverity.Warning });
        var error = await store.AppendAsync(Event() with { Severity = SystemEventSeverity.Error });

        Assert.Equal([warn.Seq, error.Seq], (await store.ListSinceAsync(info.Seq, 10)).Select(e => e.Seq));
        Assert.Equal([warn.Seq, error.Seq], (await store.ListSinceAsync(0, 10, SystemEventSeverity.Warning)).Select(e => e.Seq));
        Assert.Equal([error.Seq], (await store.ListSinceAsync(0, 10, SystemEventSeverity.Error)).Select(e => e.Seq));
    }

    [Fact]
    public async Task ExistsForJob_MatchesTypeJobAndTimeWindow()
    {
        var store = new InMemorySystemEventStore();
        var jobId = Guid.NewGuid();
        var stalledAt = DateTimeOffset.UtcNow;
        await store.AppendAsync(Event(type: SystemEventTypes.JobStalled) with { JobId = jobId, OccurredAt = stalledAt });

        // The watchdog dedup: an event newer than the job's last progress means the stall was announced.
        Assert.True(await store.ExistsForJobAsync(SystemEventTypes.JobStalled, jobId, stalledAt.AddMinutes(-30)));
        // The job progressed after the event → a new stall would alert again.
        Assert.False(await store.ExistsForJobAsync(SystemEventTypes.JobStalled, jobId, stalledAt.AddMinutes(5)));
        Assert.False(await store.ExistsForJobAsync(SystemEventTypes.JobStalled, Guid.NewGuid(), stalledAt.AddMinutes(-30)));
        Assert.False(await store.ExistsForJobAsync(SystemEventTypes.JobFailed, jobId, stalledAt.AddMinutes(-30)));
    }
}
