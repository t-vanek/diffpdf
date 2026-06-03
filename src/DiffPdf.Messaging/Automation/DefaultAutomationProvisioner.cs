using Cronos;
using DiffPdf.Core.Models;
using DiffPdf.Core.Storage;
using DiffPdf.Messaging.Scheduling;
using DiffPdf.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DiffPdf.Messaging.Automation;

/// <summary>What the provisioner did for one instance (for logging / API surfacing).</summary>
public sealed record DefaultAutomationResult(bool ScheduleCreated, bool WatchCreated, bool TriggerLaunched)
{
    public static readonly DefaultAutomationResult None = new(false, false, false);
}

/// <summary>
/// Provisions the configured <see cref="DefaultAutomationOptions">default automation</see> for an instance
/// that has just been registered for the first time.
/// </summary>
public interface IDefaultAutomationProvisioner
{
    /// <summary>
    /// Creates the configured default schedule + watch (disabled, and only when the instance has none),
    /// then optionally fires a one-time initial trigger. Idempotent and best-effort — it never throws, so a
    /// provisioning hiccup cannot undo the registration. A no-op when <c>DefaultAutomation:Enabled</c> is false.
    /// </summary>
    Task<DefaultAutomationResult> ProvisionAsync(Branch branch, ComparisonInstance instance, CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class DefaultAutomationProvisioner(
    IScheduleStore schedules,
    IWatchStore watches,
    IBatchLauncher launcher,
    IOptions<DefaultAutomationOptions> options,
    ILogger<DefaultAutomationProvisioner> logger) : IDefaultAutomationProvisioner
{
    public async Task<DefaultAutomationResult> ProvisionAsync(Branch branch, ComparisonInstance instance, CancellationToken ct = default)
    {
        var opt = options.Value;
        if (!opt.Enabled)
            return DefaultAutomationResult.None;

        bool scheduleCreated = await TryCreateScheduleAsync(opt, branch, instance, ct);
        bool watchCreated = await TryCreateWatchAsync(opt, branch, instance, ct);
        bool triggerLaunched = await TryFireInitialTriggerAsync(opt, branch, instance, ct);

        if (scheduleCreated || watchCreated || triggerLaunched)
            logger.LogInformation(
                "Default automation for {Branch}/{Instance}: schedule={Schedule}, watch={Watch}, initialTrigger={Trigger}.",
                branch.Key, instance.Key, scheduleCreated, watchCreated, triggerLaunched);

        return new DefaultAutomationResult(scheduleCreated, watchCreated, triggerLaunched);
    }

    private async Task<bool> TryCreateScheduleAsync(DefaultAutomationOptions opt, Branch branch, ComparisonInstance instance, CancellationToken ct)
    {
        if (!opt.CreateSchedule)
            return false;
        try
        {
            // Only when the instance has no schedule with this key — keeps re-registration idempotent.
            if (await schedules.GetByKeyAsync(instance.Id, opt.ScheduleKey, ct) is not null)
                return false;

            if (!TryValidateCron(opt.ScheduleCron, out string? cronError))
            {
                logger.LogWarning("Default schedule skipped for {Branch}/{Instance}: invalid DefaultAutomation:ScheduleCron '{Cron}' ({Error}).",
                    branch.Key, instance.Key, opt.ScheduleCron, cronError);
                return false;
            }

            await schedules.CreateAsync(new ComparisonSchedule
            {
                Id = Guid.NewGuid(),
                BranchId = branch.Id,
                InstanceId = instance.Id,
                BranchKey = branch.Key,
                InstanceKey = instance.Key,
                Key = opt.ScheduleKey,
                Name = "Default schedule",
                Cron = opt.ScheduleCron,
                Enabled = false, // created as a disabled template; an operator enables it when ready
            }, ct);
            return true;
        }
        catch (DuplicateKeyException)
        {
            return false; // lost a race with a concurrent provision — fine
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to provision the default schedule for {Branch}/{Instance}.", branch.Key, instance.Key);
            return false;
        }
    }

    private async Task<bool> TryCreateWatchAsync(DefaultAutomationOptions opt, Branch branch, ComparisonInstance instance, CancellationToken ct)
    {
        if (!opt.CreateWatch)
            return false;
        try
        {
            if (await watches.GetByInstanceAsync(instance.Id, ct) is not null)
                return false;

            await watches.UpsertAsync(new FolderWatch
            {
                Id = Guid.NewGuid(),
                BranchId = branch.Id,
                InstanceId = instance.Id,
                BranchKey = branch.Key,
                InstanceKey = instance.Key,
                StabilitySeconds = opt.WatchStabilitySeconds,
                Enabled = false, // created disarmed; an operator arms it when ready
            }, ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to provision the default folder-watch for {Branch}/{Instance}.", branch.Key, instance.Key);
            return false;
        }
    }

    private async Task<bool> TryFireInitialTriggerAsync(DefaultAutomationOptions opt, Branch branch, ComparisonInstance instance, CancellationToken ct)
    {
        if (!opt.FireInitialTrigger)
            return false;
        try
        {
            var result = await launcher.LaunchAsync(branch.Key, instance.Key, LaunchSpec.Default, ct);
            logger.LogInformation("Initial trigger for {Branch}/{Instance}: {Outcome}{Job}.",
                branch.Key, instance.Key, result.Outcome, result.JobId is { } id ? $" (job {id})" : "");
            return result.JobId is not null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to fire the initial trigger for {Branch}/{Instance}.", branch.Key, instance.Key);
            return false;
        }
    }

    private static bool TryValidateCron(string cron, out string? error)
    {
        try
        {
            CronExpression.Parse(cron);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
