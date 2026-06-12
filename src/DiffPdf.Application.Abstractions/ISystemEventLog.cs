using DiffPdf.Core.Models;

namespace DiffPdf.Application.Abstractions;

/// <summary>
/// Appends to the system event log and pushes the persisted event to clients. Best-effort by contract:
/// implementations log and swallow failures, so recording an event can never break the domain flow that
/// raised it (a job completion must not fail because the observability write did).
/// </summary>
public interface ISystemEventLog
{
    Task AppendAsync(SystemEvent evt, CancellationToken ct = default);
}

/// <summary>No-op log for tests and contexts without persistence.</summary>
public sealed class NullSystemEventLog : ISystemEventLog
{
    public Task AppendAsync(SystemEvent evt, CancellationToken ct = default) => Task.CompletedTask;
}
