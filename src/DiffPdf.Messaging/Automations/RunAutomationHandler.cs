using DiffPdf.Application.Abstractions;
using DiffPdf.Core.Models;
using DiffPdf.Persistence;
using Microsoft.Extensions.Logging;
using Wolverine.Attributes;

namespace DiffPdf.Messaging.Automations;

/// <summary>
/// Executes one event-triggered automation run. The command rode a durable local queue, so the run
/// survives a restart between the trigger and the execution. A missing or disabled automation is simply
/// acknowledged; an in-flight run (the claim is held) is skipped — the debounce already throttled the
/// trigger, and the running pipeline covers the same intent.
/// </summary>
public static class RunAutomationHandler
{
    // Wolverine's default 60s execution timeout would kill a long pipeline mid-step; the runner enforces
    // the automation's own per-attempt TimeoutSeconds (max 86400), so the message timeout only needs to
    // outlive the worst-case run. INVARIANT: must exceed TimeoutSeconds * MaxAttempts + retry delays.
    [MessageTimeout(2 * 86_400)]
    public static async Task Handle(
        RunAutomation command,
        IAutomationStore store,
        IAutomationRunner runner,
        ILogger logger,
        CancellationToken ct)
    {
        var automation = await store.GetAsync(command.AutomationId, ct);
        if (automation is null || !automation.Enabled)
        {
            logger.LogInformation(
                "RunAutomation {AutomationId}: automation is gone or disabled; dropping the trigger.",
                command.AutomationId);
            return;
        }

        if (!await store.TryBeginRunAsync(
                automation.Id, DateTimeOffset.UtcNow, advanceNextRunAtTo: null,
                AutomationCadence.StaleClaimWindow(automation), ct))
        {
            logger.LogInformation(
                "Automation {Key}: event-triggered run skipped — another run is in flight.", automation.Key);
            return;
        }

        await runner.RunAsync(automation, AutomationTriggerKind.Event, command.TriggerDetail, command.ChainDepth, ct);
    }
}
