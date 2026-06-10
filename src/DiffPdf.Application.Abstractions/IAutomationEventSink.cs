using DiffPdf.Core.Models;

namespace DiffPdf.Application.Abstractions;

/// <summary>
/// Receives domain notification events and fires event-triggered automations: matches enabled automations
/// whose <see cref="Automation.EventTriggers"/> contain the event and whose scope matches (subscription
/// semantics — empty scope filter = any), claims the per-automation debounce atomically, and publishes a
/// durable run command. Best-effort: implementations log and swallow failures so event sources are never
/// disrupted.
/// </summary>
public interface IAutomationEventSink
{
    /// <summary>
    /// Announces <paramref name="event"/> to event-triggered automations. <paramref name="sourceAutomationId"/>
    /// excludes the automation that raised the event from triggering itself; <paramref name="chainDepth"/>
    /// caps event-trigger cascades.
    /// </summary>
    Task PublishAsync(
        NotificationEvent @event,
        string? branchKey,
        string? instanceKey,
        string detail,
        Guid? sourceAutomationId,
        int chainDepth,
        CancellationToken ct = default);
}
