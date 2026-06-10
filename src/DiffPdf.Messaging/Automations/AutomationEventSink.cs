using DiffPdf.Application.Abstractions;
using DiffPdf.Core.Models;
using DiffPdf.Persistence;
using Microsoft.Extensions.Logging;
using Wolverine;

namespace DiffPdf.Messaging.Automations;

/// <summary>
/// Default <see cref="IAutomationEventSink"/>: matches enabled automations whose
/// <see cref="Automation.EventTriggers"/> contain the event and whose scope matches the event's
/// branch/instance (subscription semantics), atomically claims the per-automation debounce, and publishes
/// a durable <see cref="RunAutomation"/> command per claimed automation. Guards against cascades three
/// ways: the source automation never triggers itself, every hop carries a chain depth capped at
/// <see cref="MaxChainDepth"/>, and the durable debounce throttles repeats. Best-effort: failures are
/// logged and never disrupt the event source.
/// </summary>
public sealed class AutomationEventSink(
    IAutomationStore automations,
    IMessageBus bus,
    ILogger<AutomationEventSink> logger) : IAutomationEventSink
{
    internal const int MaxChainDepth = 4;

    public async Task PublishAsync(
        NotificationEvent @event,
        string? branchKey,
        string? instanceKey,
        string detail,
        Guid? sourceAutomationId,
        int chainDepth,
        CancellationToken ct = default)
    {
        try
        {
            if (chainDepth > MaxChainDepth)
            {
                logger.LogWarning(
                    "Automation event chain depth {Depth} exceeded for {Event}; not triggering further automations.",
                    chainDepth, @event);
                return;
            }

            var candidates = (await automations.ListEnabledAsync(ct))
                .Where(a => a.EventTriggers.Contains(@event)
                            && a.Id != sourceAutomationId
                            && ScopeMatches(a, branchKey, instanceKey))
                .ToList();
            if (candidates.Count == 0)
                return;

            string triggerDetail = TriggerDetail(@event, detail);
            foreach (var automation in candidates)
            {
                var debounce = TimeSpan.FromSeconds(Math.Max(0, automation.EventDebounceSeconds));
                if (!await automations.TryClaimEventTriggerAsync(automation.Id, DateTimeOffset.UtcNow, debounce, ct))
                    continue; // within the debounce window (or just disabled/deleted)

                await bus.PublishAsync(new RunAutomation(automation.Id, triggerDetail, chainDepth));
                logger.LogInformation(
                    "Automation {Key} triggered by {Event} (depth {Depth}).", automation.Key, @event, chainDepth);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Automation event sink failed for {Event}.", @event);
        }
    }

    /// <summary>The automation's scope must contain the event's origin; a global automation hears everything.</summary>
    private static bool ScopeMatches(Automation a, string? branchKey, string? instanceKey) => a.ScopeKind switch
    {
        AutomationScopeKind.Global => true,
        AutomationScopeKind.Branch =>
            branchKey is not null && string.Equals(a.BranchKey, branchKey, StringComparison.OrdinalIgnoreCase),
        _ =>
            branchKey is not null && string.Equals(a.BranchKey, branchKey, StringComparison.OrdinalIgnoreCase)
            && instanceKey is not null && string.Equals(a.InstanceKey, instanceKey, StringComparison.OrdinalIgnoreCase),
    };

    private static string TriggerDetail(NotificationEvent ev, string detail)
    {
        var summary = detail.ReplaceLineEndings(" ").Trim();
        if (summary.Length > 160)
            summary = summary[..160] + "…";
        return summary.Length == 0 ? $"event:{ev}" : $"event:{ev} ({summary})";
    }
}
