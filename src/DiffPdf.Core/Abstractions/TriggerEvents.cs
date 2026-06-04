namespace DiffPdf.Core.Abstractions;

/// <summary>
/// A real-time trigger / batch / comparison event (also the SignalR payload). <see cref="EventType"/> is a
/// dotted name such as <c>trigger.run.requested</c>, <c>batch.queued</c> or <c>comparison.completed</c>.
/// Always published <b>after</b> the corresponding state change is committed.
/// </summary>
public sealed record TriggerEvent(
    string EventType,
    Guid TriggerId,
    Guid? BatchJobId = null,
    Guid? BranchId = null,
    Guid? InstanceId = null,
    string? BranchKey = null,
    string? InstanceKey = null,
    string? Status = null,
    string? Result = null,
    DateTimeOffset? StartedAt = null,
    DateTimeOffset? FinishedAt = null,
    long? DurationMs = null,
    string? Source = null,
    string? Message = null,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    string? ResultReference = null);

/// <summary>
/// Pushes trigger / batch / comparison events to interested clients (SignalR), routed by trigger, job,
/// instance and branch. A no-op is acceptable (e.g. when no realtime transport is wired in).
/// </summary>
public interface ITriggerEventPublisher
{
    Task PublishAsync(TriggerEvent evt, CancellationToken ct = default);
}

/// <summary>Default no-op publisher used when no realtime transport is available (worker-only / tests).</summary>
public sealed class NullTriggerEventPublisher : ITriggerEventPublisher
{
    public Task PublishAsync(TriggerEvent evt, CancellationToken ct = default) => Task.CompletedTask;
}
