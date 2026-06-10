namespace DiffPdf.Messaging.Automations;

/// <summary>
/// Durable command: execute one automation now. Published by <see cref="AutomationEventSink"/> when a
/// domain event matches an automation's event triggers (after the debounce claim), and handled by
/// <c>RunAutomationHandler</c>. Riding Wolverine's durable local queue gives the event-triggered run
/// crash-survival and exactly-once-ish processing for free. <paramref name="ChainDepth"/> carries the
/// event-cascade depth so runaway automation chains are capped.
/// </summary>
public sealed record RunAutomation(Guid AutomationId, string? TriggerDetail, int ChainDepth);
