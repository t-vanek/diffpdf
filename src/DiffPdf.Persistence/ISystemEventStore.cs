using DiffPdf.Core.Models;

namespace DiffPdf.Persistence;

/// <summary>
/// Persistence for the append-only system event log. <see cref="SystemEvent.Seq"/> is assigned on append
/// (monotonic); reads are cursor-based (<c>sinceSeq</c>) so clients can replay missed events after a reconnect.
/// </summary>
public interface ISystemEventStore
{
    /// <summary>Appends the event and returns it with its assigned <see cref="SystemEvent.Seq"/>.</summary>
    Task<SystemEvent> AppendAsync(SystemEvent evt, CancellationToken ct = default);

    /// <summary>Events with <c>Seq &gt; sinceSeq</c>, oldest first, optionally filtered to at least <paramref name="minSeverity"/>.</summary>
    Task<IReadOnlyList<SystemEvent>> ListSinceAsync(long sinceSeq, int limit, SystemEventSeverity? minSeverity = null, CancellationToken ct = default);

    /// <summary>The newest events (newest first) — the initial notification-center fill.</summary>
    Task<IReadOnlyList<SystemEvent>> ListRecentAsync(int limit, CancellationToken ct = default);

    /// <summary>True when an event of <paramref name="type"/> for <paramref name="jobId"/> occurred after <paramref name="after"/> — the stall-alert dedup.</summary>
    Task<bool> ExistsForJobAsync(string type, Guid jobId, DateTimeOffset after, CancellationToken ct = default);

    /// <summary>Deletes events older than the cutoff; returns the number removed (retention).</summary>
    Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default);
}
