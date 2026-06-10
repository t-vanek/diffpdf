using System.Diagnostics;
using DiffPdf.Application.Abstractions;
using DiffPdf.Core.Models;
using DiffPdf.Notifications;
using DiffPdf.Persistence;
using Microsoft.Extensions.Logging;

namespace DiffPdf.Messaging.Automations;

/// <summary>
/// Executes one automation run — the pipeline shared by the engine tick (Schedule), the event-trigger
/// handler (Event) and the run-now endpoint (Manual). Per attempt it runs the steps in order under the
/// automation's per-attempt timeout, failing fast on a Failed step (a Warning continues) and recording one
/// <see cref="AutomationRun"/> row; a Failed attempt is retried up to <see cref="Automation.MaxAttempts"/>.
/// The caller must have claimed the run via <c>IAutomationStore.TryBeginRunAsync</c>; the runner always
/// releases the claim (<c>CompleteRunAsync</c> with <see cref="CancellationToken.None"/>, so a shutdown
/// mid-run never wedges the automation). Non-Ok outcomes raise the automation's notification events; a
/// failure streak crossing <see cref="Automation.FailureThreshold"/> escalates once via
/// <see cref="NotificationEvent.AutomationFailing"/>; an Ok run after a non-Ok one raises the recovery
/// event. Every raised event also flows into <see cref="IAutomationEventSink"/> so other automations can
/// trigger on it (the source automation never triggers itself).
/// </summary>
public sealed class AutomationRunner(
    IEnumerable<IAutomationStepExecutor> executors,
    IAutomationStore automations,
    IAutomationRunStore runs,
    INotificationDispatcher dispatcher,
    IAutomationEventSink eventSink,
    AutomationMetrics metrics,
    ILogger<AutomationRunner> logger) : IAutomationRunner
{
    public async Task<AutomationRun> RunAsync(
        Automation automation,
        AutomationTriggerKind trigger,
        string? triggerDetail,
        int chainDepth,
        CancellationToken ct = default)
    {
        int maxAttempts = Math.Max(1, automation.MaxAttempts);
        AutomationRun? lastRun = null;

        try
        {
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                var run = await RunAttemptAsync(automation, trigger, triggerDetail, attempt, ct);
                lastRun = run;
                metrics.RecordRun(run.Outcome, trigger, run.CompletedAt!.Value - run.StartedAt);

                try
                {
                    await runs.AddAsync(run, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Automation {Key}: could not record run history.", automation.Key);
                }

                if (run.Outcome != AutomationRunOutcome.Failed || attempt == maxAttempts)
                    break;

                logger.LogInformation(
                    "Automation {Key}: attempt {Attempt}/{Max} failed, retrying in {Delay}s.",
                    automation.Key, attempt, maxAttempts, automation.RetryDelaySeconds);
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, automation.RetryDelaySeconds)), ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown mid-run: release the claim without counting a failure — the attempt was abandoned,
            // not failed. The schedule's NextRunAt was already advanced by the claim, so no re-fire storm.
            await TryCompleteAsync(automation, automation.LastOutcome ?? AutomationRunOutcome.Ok, automation.ConsecutiveFailures);
            throw;
        }

        var finalOutcome = lastRun!.Outcome;
        int consecutiveFailures = finalOutcome == AutomationRunOutcome.Failed ? automation.ConsecutiveFailures + 1 : 0;
        await TryCompleteAsync(automation, finalOutcome, consecutiveFailures);

        logger.LogInformation("Automation {Key} ran ({Trigger}): {Outcome}.", automation.Key, trigger, finalOutcome);
        await NotifyAsync(automation, lastRun, consecutiveFailures, chainDepth, ct);
        return lastRun;
    }

    private async Task<AutomationRun> RunAttemptAsync(
        Automation automation,
        AutomationTriggerKind trigger,
        string? triggerDetail,
        int attempt,
        CancellationToken ct)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var results = new List<AutomationStepResult>();
        var outcome = AutomationRunOutcome.Ok;

        int currentIndex = 0;
        var currentStep = automation.Steps[0];

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, automation.TimeoutSeconds)));

        try
        {
            for (int i = 0; i < automation.Steps.Count; i++)
            {
                currentIndex = i;
                currentStep = automation.Steps[i];
                var executor = executors.FirstOrDefault(e => e.Type == currentStep.Type);

                var sw = Stopwatch.StartNew();
                StepResult stepResult;
                if (executor is null)
                {
                    stepResult = StepResult.Failed($"No executor registered for step type {currentStep.Type}.");
                }
                else
                {
                    try
                    {
                        stepResult = await executor.ExecuteAsync(automation, currentStep, timeoutCts.Token);
                    }
                    catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
                    {
                        throw; // per-attempt timeout (or shutdown) — handled below
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Automation {Key} step {Index} ({Type}) threw.", automation.Key, i, currentStep.Type);
                        stepResult = StepResult.Failed($"step threw: {ex.Message}");
                    }
                }
                sw.Stop();

                results.Add(new AutomationStepResult(
                    i, currentStep.Type, currentStep.Name, stepResult.Outcome, stepResult.Detail, sw.Elapsed.TotalSeconds));
                metrics.RecordStep(currentStep.Type, stepResult.Outcome, sw.Elapsed);

                outcome = Worst(outcome, stepResult.Outcome);
                if (stepResult.Outcome == AutomationRunOutcome.Failed)
                    break; // fail fast; a Warning continues
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The per-attempt timeout fired (the outer token is still live).
            outcome = AutomationRunOutcome.Failed;
            var elapsed = DateTimeOffset.UtcNow - startedAt;
            results.Add(new AutomationStepResult(
                currentIndex, currentStep.Type, currentStep.Name, AutomationRunOutcome.Failed,
                $"timed out after {automation.TimeoutSeconds}s.", elapsed.TotalSeconds));
            metrics.RecordStep(currentStep.Type, AutomationRunOutcome.Failed, elapsed);
            logger.LogWarning(
                "Automation {Key}: attempt {Attempt} timed out after {Timeout}s at step {Index} ({Type}).",
                automation.Key, attempt, automation.TimeoutSeconds, currentIndex, currentStep.Type);
        }

        return new AutomationRun
        {
            Id = Guid.NewGuid(),
            AutomationId = automation.Id,
            Trigger = trigger,
            TriggerDetail = triggerDetail,
            Attempt = attempt,
            StartedAt = startedAt,
            CompletedAt = DateTimeOffset.UtcNow,
            Outcome = outcome,
            Detail = ComposeSummary(results),
            StepResults = results,
        };
    }

    /// <summary>Best-effort claim release + state write; never throws (a wedged claim self-heals via staleAfter).</summary>
    private async Task TryCompleteAsync(Automation automation, AutomationRunOutcome outcome, int consecutiveFailures)
    {
        try
        {
            await automations.CompleteRunAsync(automation.Id, DateTimeOffset.UtcNow, outcome, consecutiveFailures, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Automation {Key}: could not record run completion.", automation.Key);
        }
    }

    private async Task NotifyAsync(
        Automation automation, AutomationRun run, int consecutiveFailures, int chainDepth, CancellationToken ct)
    {
        try
        {
            if (run.Outcome != AutomationRunOutcome.Ok)
            {
                // Raise each event the automation is configured to emit on failure.
                foreach (var ev in automation.Events)
                    await RaiseAsync(automation, ev, run, chainDepth, ct);

                // Escalate exactly once, when the failure streak crosses the threshold.
                if (run.Outcome == AutomationRunOutcome.Failed
                    && automation.FailureThreshold > 0
                    && consecutiveFailures == automation.FailureThreshold)
                    await RaiseAsync(automation, NotificationEvent.AutomationFailing, run, chainDepth, ct);
            }
            else if (automation.LastOutcome is AutomationRunOutcome.Failed or AutomationRunOutcome.Warning)
            {
                // It was failing and now passes — announce the recovery (filtered by subscription).
                await RaiseAsync(automation, NotificationEvent.AutomationRecovered, run, chainDepth, ct);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Automation {Key}: notification dispatch failed.", automation.Key);
        }
    }

    private async Task RaiseAsync(
        Automation automation, NotificationEvent ev, AutomationRun run, int chainDepth, CancellationToken ct)
    {
        var detail = run.Detail ?? string.Empty;
        await dispatcher.DispatchAsync(new AutomationNotification(
            ev, automation.Key, automation.Name, automation.BranchKey, automation.InstanceKey,
            run.Outcome, detail, run.CompletedAt!.Value), ct);
        await eventSink.PublishAsync(
            ev, automation.BranchKey, automation.InstanceKey, detail,
            sourceAutomationId: automation.Id, chainDepth: chainDepth + 1, ct);
    }

    private static AutomationRunOutcome Worst(AutomationRunOutcome a, AutomationRunOutcome b) =>
        (AutomationRunOutcome)Math.Max((int)a, (int)b);

    private static string ComposeSummary(IReadOnlyList<AutomationStepResult> results) => results.Count switch
    {
        0 => "no steps executed.",
        1 => results[0].Detail,
        _ => string.Join('\n', results.Select(r =>
            $"[{r.Index + 1}] {r.Name ?? r.Type.ToString()} — {r.Outcome}: {r.Detail}")),
    };
}
