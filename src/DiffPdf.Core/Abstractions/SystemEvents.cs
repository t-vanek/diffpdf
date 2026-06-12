using DiffPdf.Core.Models;

namespace DiffPdf.Core.Abstractions;

/// <summary>Pushes a persisted <see cref="SystemEvent"/> to connected clients in real time (SignalR).</summary>
public interface ISystemEventPublisher
{
    Task PublishAsync(SystemEvent evt, CancellationToken ct = default);
}

/// <summary>Default no-op publisher used when no realtime transport is available (worker-only / tests).</summary>
public sealed class NullSystemEventPublisher : ISystemEventPublisher
{
    public Task PublishAsync(SystemEvent evt, CancellationToken ct = default) => Task.CompletedTask;
}
