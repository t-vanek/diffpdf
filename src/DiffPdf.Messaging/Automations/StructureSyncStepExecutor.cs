using DiffPdf.Core.Models;
using DiffPdf.Messaging.ScopeSync;

namespace DiffPdf.Messaging.Automations;

/// <summary>
/// Reconciles the configured scope root with the branch/instance records in the database
/// (<see cref="IScopeSyncService"/>, applying changes). Reports drift — folders registered, missing
/// folders created, or instances whose base lies outside the root — as a failure so it is notified.
/// </summary>
public sealed class StructureSyncStepExecutor(IScopeSyncService scopeSync) : IAutomationStepExecutor
{
    public AutomationStepType Type => AutomationStepType.StructureSync;

    public async Task<StepResult> ExecuteAsync(Automation automation, AutomationStep step, CancellationToken ct)
    {
        var report = await scopeSync.SynchronizeAsync(apply: true, ct);

        if (!report.Ok)
            return StepResult.Failed(report.Error ?? "Scope sync could not reconcile the root.");

        int registered = report.RegisteredBranches + report.RegisteredInstances;
        int missing = report.MissingFolders.Count;
        int outOfRoot = report.OutOfRoot.Count;

        if (outOfRoot > 0)
            return StepResult.Failed(
                $"{outOfRoot} instance(s) have a base path outside the sync root (manual fix needed); " +
                $"registered {registered}, created {missing} missing folder(s).");

        if (registered > 0 || missing > 0)
            return StepResult.Warning(
                $"drift reconciled: registered {registered}, created {missing} missing folder(s).");

        return StepResult.Ok($"scope in sync with root '{report.Root}'.");
    }
}
