using DiffPdf.Core.Models;

namespace DiffPdf.Application.Abstractions;

/// <summary>Runs a single automation pipeline: executes its steps, records the attempt rows, updates the
/// automation's engine state, and raises notifications/events.</summary>
public interface IAutomationRunner
{
    /// <summary>
    /// Executes <paramref name="automation"/> now (shared by the engine tick, the event-trigger handler and
    /// the run-now endpoint). The caller must have claimed the run via
    /// <c>IAutomationStore.TryBeginRunAsync</c>; the runner always releases the claim. Failures become run
    /// outcomes — the method only throws on cancellation. Returns the final (last-attempt) run.
    /// <paramref name="chainDepth"/> counts event-trigger cascades; events raised by this run carry it +1.
    /// </summary>
    Task<AutomationRun> RunAsync(
        Automation automation,
        AutomationTriggerKind trigger,
        string? triggerDetail,
        int chainDepth,
        CancellationToken ct = default);
}
