using DiffPdf.Core.Models;

namespace DiffPdf.Messaging.Automations;

/// <summary>The outcome of running one automation step, with a human-readable detail.</summary>
public sealed record StepResult(AutomationRunOutcome Outcome, string Detail)
{
    public static StepResult Ok(string detail) => new(AutomationRunOutcome.Ok, detail);
    public static StepResult Warning(string detail) => new(AutomationRunOutcome.Warning, detail);
    public static StepResult Failed(string detail) => new(AutomationRunOutcome.Failed, detail);
}

/// <summary>Executes one <see cref="AutomationStepType"/> of automation step. Resolved by the runner via <see cref="Type"/>.</summary>
public interface IAutomationStepExecutor
{
    /// <summary>The step type this executor handles.</summary>
    AutomationStepType Type { get; }

    /// <summary>
    /// Runs the step and returns its outcome. Scope comes from <paramref name="automation"/>, parameters
    /// from <paramref name="step"/>. Must not throw for expected failures — return a Failed result.
    /// </summary>
    Task<StepResult> ExecuteAsync(Automation automation, AutomationStep step, CancellationToken ct);
}
