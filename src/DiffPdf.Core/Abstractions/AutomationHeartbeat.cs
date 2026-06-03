using System.Collections.Concurrent;

namespace DiffPdf.Core.Abstractions;

/// <summary>
/// In-process registry of the last activity of each automation background service (scheduler,
/// folder-watch, retention, stale-task recovery). Each service records a tick; the operational
/// status endpoint reads the snapshot. It is <b>per-replica</b> — each process has its own — so it
/// reflects the services running in <i>this</i> replica; the leader identity and the queue backlog
/// come from the shared database instead.
/// </summary>
public interface IAutomationHeartbeat
{
    /// <summary>
    /// Records a tick for <paramref name="service"/>. <paramref name="leaderActive"/> marks ticks
    /// where this replica held the automation lease (i.e. actually did work, not just stood by);
    /// <paramref name="error"/> records a failed tick.
    /// </summary>
    void Record(string service, bool leaderActive, string? error = null);

    /// <summary>A point-in-time copy of every known service's last activity.</summary>
    IReadOnlyList<ServiceHeartbeat> Snapshot();
}

/// <summary>Last-activity snapshot for one background service.</summary>
public sealed record ServiceHeartbeat(
    string Service,
    DateTimeOffset? LastTickAt,
    DateTimeOffset? LastLeaderActiveAt,
    long TickCount,
    string? LastError,
    DateTimeOffset? LastErrorAt);

/// <summary>Thread-safe in-memory <see cref="IAutomationHeartbeat"/> (registered as a singleton).</summary>
public sealed class AutomationHeartbeat : IAutomationHeartbeat
{
    private sealed class Entry
    {
        public DateTimeOffset? LastTickAt;
        public DateTimeOffset? LastLeaderActiveAt;
        public long TickCount;
        public string? LastError;
        public DateTimeOffset? LastErrorAt;
    }

    private readonly ConcurrentDictionary<string, Entry> _entries = new();

    public void Record(string service, bool leaderActive, string? error = null)
    {
        var now = DateTimeOffset.UtcNow;
        var entry = _entries.GetOrAdd(service, static _ => new Entry());
        lock (entry)
        {
            entry.LastTickAt = now;
            entry.TickCount++;
            if (leaderActive)
                entry.LastLeaderActiveAt = now;
            if (error is not null)
            {
                entry.LastError = error;
                entry.LastErrorAt = now;
            }
        }
    }

    public IReadOnlyList<ServiceHeartbeat> Snapshot() =>
        _entries
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv =>
            {
                var e = kv.Value;
                lock (e)
                {
                    return new ServiceHeartbeat(kv.Key, e.LastTickAt, e.LastLeaderActiveAt, e.TickCount, e.LastError, e.LastErrorAt);
                }
            })
            .ToList();
}
