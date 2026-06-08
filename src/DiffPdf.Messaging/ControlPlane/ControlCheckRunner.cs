using DiffPdf.Application.Abstractions;
using DiffPdf.Core.Models;
using DiffPdf.Notifications;
using DiffPdf.Persistence;
using Microsoft.Extensions.Logging;

namespace DiffPdf.Messaging.ControlPlane;

/// <inheritdoc />
public sealed class ControlCheckRunner(
    IEnumerable<IControlCheckExecutor> executors,
    IControlCheckStore checks,
    IControlCheckRunStore runs,
    INotificationDispatcher dispatcher,
    ILogger<ControlCheckRunner> logger) : IControlCheckRunner
{
    public async Task<ControlCheckRun> RunAsync(ControlCheck check, CancellationToken ct = default)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var executor = executors.FirstOrDefault(e => e.Type == check.Type);

        CheckResult result;
        if (executor is null)
        {
            result = CheckResult.Failed($"No executor registered for check type {check.Type}.");
        }
        else
        {
            try
            {
                result = await executor.ExecuteAsync(check, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Control check {Key} threw.", check.Key);
                result = CheckResult.Failed($"check threw: {ex.Message}");
            }
        }

        var run = new ControlCheckRun
        {
            Id = Guid.NewGuid(),
            CheckId = check.Id,
            StartedAt = startedAt,
            CompletedAt = DateTimeOffset.UtcNow,
            Outcome = result.Outcome,
            Detail = result.Detail,
        };
        await runs.AddAsync(run, ct);
        await checks.TouchLastRunAsync(check.Id, run.CompletedAt!.Value, result.Outcome, ct);

        await DispatchAsync(check, result, run.CompletedAt!.Value, ct);

        logger.LogInformation("Control check {Key} ran: {Outcome}.", check.Key, result.Outcome);
        return run;
    }

    private async Task DispatchAsync(ControlCheck check, CheckResult result, DateTimeOffset at, CancellationToken ct)
    {
        try
        {
            if (result.Outcome != CheckRunOutcome.Ok)
            {
                // Raise each event the check is configured to emit on failure.
                foreach (var ev in check.Events)
                    await dispatcher.DispatchAsync(Notification(check, ev, result, at), ct);
            }
            else if (check.LastOutcome is CheckRunOutcome.Failed or CheckRunOutcome.Warning)
            {
                // It was failing and now passes — announce the recovery (filtered by subscription).
                await dispatcher.DispatchAsync(Notification(check, NotificationEvent.CheckRecovered, result, at), ct);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Control check {Key}: notification dispatch failed.", check.Key);
        }
    }

    private static CheckNotification Notification(ControlCheck check, NotificationEvent ev, CheckResult result, DateTimeOffset at) =>
        new(ev, check.Key, check.Name, check.Type, check.BranchKey, check.InstanceKey, result.Outcome, result.Detail, at);
}
