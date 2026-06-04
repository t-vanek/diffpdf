using DiffPdf.Core.Models;
using DiffPdf.Persistence;
using Microsoft.Extensions.Logging;

namespace DiffPdf.Messaging.ControlPlane;

/// <summary>
/// Prunes old <b>database rows</b> — finished jobs and their file-pair tasks, plus trigger-run and audit
/// history — older than the configured retention window, so the relational store does not grow without bound.
/// A job row is removed only once its on-disk artifacts have already been pruned (see
/// <see cref="RetentionCheckExecutor"/>), so row deletion can never orphan reports on disk. The companion
/// artifact-retention check therefore runs with a shorter window; this one keeps DB history longer. Parameters:
/// <c>retentionDays</c> (default 90) and <c>maxPerTick</c> (default 1000, bounds the job/task batch per run).
/// </summary>
public sealed class DbRowRetentionCheckExecutor(
    IJobStore jobs,
    IFilePairTaskStore tasks,
    ITriggerRunStore triggerRuns,
    IAuditLogStore auditLog,
    ILogger<DbRowRetentionCheckExecutor> logger) : IControlCheckExecutor
{
    public CheckType Type => CheckType.DbRowRetention;

    public async Task<CheckResult> ExecuteAsync(ControlCheck check, CancellationToken ct)
    {
        int retentionDays = GetInt(check, "retentionDays", 90);
        int maxPerTick = GetInt(check, "maxPerTick", 1000);
        if (retentionDays < 0)
            return CheckResult.Failed($"invalid retentionDays '{retentionDays}'.");

        var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromDays(retentionDays);

        // Jobs (+ their tasks) first: collect the prune set (artifacts already pruned), delete the tasks, then
        // the jobs. No FK links these tables, so the order is for the join logic, not referential integrity.
        var jobIds = await jobs.ListPrunableRowsAsync(cutoff, maxPerTick, ct);
        int taskRows = jobIds.Count == 0 ? 0 : await tasks.DeleteForJobsAsync(jobIds, ct);
        int jobRows = jobIds.Count == 0 ? 0 : await jobs.DeleteByIdsAsync(jobIds, ct);

        // Independent append-only history, pruned by its own timestamp.
        int runRows = await triggerRuns.DeleteStartedBeforeAsync(cutoff, ct);
        int auditRows = await auditLog.DeleteBeforeAsync(cutoff, ct);

        if (taskRows + jobRows + runRows + auditRows == 0)
            return CheckResult.Ok($"nothing to prune (older than {retentionDays} d).");

        logger.LogInformation(
            "DB-row retention: pruned {Jobs} job(s), {Tasks} task(s), {Runs} trigger-run(s), {Audit} audit row(s) before {Cutoff:u}.",
            jobRows, taskRows, runRows, auditRows, cutoff);
        return CheckResult.Ok($"pruned {jobRows} job(s), {taskRows} task(s), {runRows} trigger-run(s), {auditRows} audit row(s) before {cutoff:u}.");
    }

    private static int GetInt(ControlCheck check, string key, int fallback) =>
        check.Parameters.TryGetValue(key, out var raw) && int.TryParse(raw, out var value) ? value : fallback;
}
