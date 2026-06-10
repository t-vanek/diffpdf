using DiffPdf.Core.Models;
using DiffPdf.Persistence;
using Microsoft.Extensions.Logging;

namespace DiffPdf.Messaging.Automations;

/// <summary>
/// Prunes old <b>database rows</b> — finished jobs and their file-pair tasks, plus trigger-run, audit and
/// automation-run history — older than the configured retention window, so the relational store does not
/// grow without bound. A job row is removed only once its on-disk artifacts have already been pruned (see
/// <see cref="RetentionStepExecutor"/>), so row deletion can never orphan reports on disk. The companion
/// artifact-retention automation therefore runs with a shorter window; this one keeps DB history longer.
/// Parameters: <c>retentionDays</c> (default 90) and <c>maxPerTick</c> (default 1000, bounds the job/task
/// batch per run).
/// </summary>
public sealed class DbRowRetentionStepExecutor(
    IJobStore jobs,
    IFilePairTaskStore tasks,
    ITriggerRunStore triggerRuns,
    IAuditLogStore auditLog,
    IAutomationRunStore automationRuns,
    ILogger<DbRowRetentionStepExecutor> logger) : IAutomationStepExecutor
{
    public AutomationStepType Type => AutomationStepType.DbRowRetention;

    public async Task<StepResult> ExecuteAsync(Automation automation, AutomationStep step, CancellationToken ct)
    {
        int retentionDays = StepParameters.GetInt(step, "retentionDays", 90);
        int maxPerTick = StepParameters.GetInt(step, "maxPerTick", 1000);
        if (retentionDays < 0)
            return StepResult.Failed($"invalid retentionDays '{retentionDays}'.");

        var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromDays(retentionDays);

        // Jobs (+ their tasks) first: collect the prune set (artifacts already pruned), delete the tasks, then
        // the jobs. No FK links these tables, so the order is for the join logic, not referential integrity.
        var jobIds = await jobs.ListPrunableRowsAsync(cutoff, maxPerTick, ct);
        int taskRows = jobIds.Count == 0 ? 0 : await tasks.DeleteForJobsAsync(jobIds, ct);
        int jobRows = jobIds.Count == 0 ? 0 : await jobs.DeleteByIdsAsync(jobIds, ct);

        // Independent append-only history, pruned by its own timestamp.
        int runRows = await triggerRuns.DeleteStartedBeforeAsync(cutoff, ct);
        int auditRows = await auditLog.DeleteBeforeAsync(cutoff, ct);
        int automationRunRows = await automationRuns.DeleteStartedBeforeAsync(cutoff, ct);

        if (taskRows + jobRows + runRows + auditRows + automationRunRows == 0)
            return StepResult.Ok($"nothing to prune (older than {retentionDays} d).");

        logger.LogInformation(
            "DB-row retention: pruned {Jobs} job(s), {Tasks} task(s), {Runs} trigger-run(s), {Audit} audit row(s), {AutomationRuns} automation run(s) before {Cutoff:u}.",
            jobRows, taskRows, runRows, auditRows, automationRunRows, cutoff);
        return StepResult.Ok(
            $"pruned {jobRows} job(s), {taskRows} task(s), {runRows} trigger-run(s), {auditRows} audit row(s), {automationRunRows} automation run(s) before {cutoff:u}.");
    }
}
