using DiffPdf.Application.Abstractions;
using DiffPdf.Core.Abstractions;
using DiffPdf.Core.Models;
using DiffPdf.Persistence;
using Microsoft.Extensions.Logging;

namespace DiffPdf.Messaging;

/// <summary>
/// Default <see cref="ISystemEventLog"/>: appends to the store (which assigns the Seq cursor), then pushes
/// the persisted event to connected clients. Best-effort end to end — a store or publisher failure is logged
/// and swallowed, never disrupting the domain flow that raised the event. The push happens only after a
/// successful append, so a client never sees a Seq it cannot later replay from the REST cursor.
/// </summary>
public sealed class SystemEventLog(
    ISystemEventStore store,
    ISystemEventPublisher publisher,
    ILogger<SystemEventLog> logger) : ISystemEventLog
{
    public async Task AppendAsync(SystemEvent evt, CancellationToken ct = default)
    {
        SystemEvent persisted;
        try
        {
            // CancellationToken.None: events typically record terminal facts (completion, failure, recovery)
            // from paths that already committed — a shutdown must not lose the trail mid-append.
            persisted = await store.AppendAsync(evt, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "System event append failed ({Type}: {Message}).", evt.Type, evt.Message);
            return;
        }

        try
        {
            await publisher.PublishAsync(persisted, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Missed pushes are recovered by the client's sinceSeq replay on its next reconnect/poll.
            logger.LogDebug(ex, "System event realtime push failed (seq {Seq}).", persisted.Seq);
        }
    }
}
