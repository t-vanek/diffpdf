using DiffPdf.Core.Models;
using DiffPdf.Persistence;

namespace DiffPdf.Application.Stats;

/// <summary>Job counts by status for one scope.</summary>
public sealed record JobStats(int Queued, int Running, int Paused, int Completed, int Failed);

/// <summary>Control-check counts for one scope (Running has no persistent state today, so it is 0).</summary>
public sealed record CheckStats(int Active, int Running, int Completed, int Failed);

/// <summary>Automation counts for one scope — the umbrella over every automation type (control checks are the only type today).</summary>
public sealed record AutomationStats(int Active, int Running, int Paused, int Completed, int Failed);

/// <summary>Comparison (file-pair) counts by status for one scope. Paused/Stopped are not modelled, so they are omitted.</summary>
public sealed record ComparisonStats(int Waiting, int Running, int Completed, int Failed, int Skipped);

/// <summary>Aggregated statistics for a single branch or instance scope (jobs, checks, automations, comparisons).</summary>
public sealed record ScopeStatsResponse(JobStats Jobs, CheckStats Checks, AutomationStats Automations, ComparisonStats Comparisons);

/// <summary>
/// Computes the per-branch / per-instance statistics shown in the branch and instance detail views:
/// jobs, checks, automations and comparisons. Aggregates from the existing stores. The scope's jobs are
/// loaded once — that single list yields both the job-status breakdown and the job ids used to count the
/// comparison (file-pair) tasks by status. Checks bound to the scope are filtered from the full list.
/// </summary>
public interface IScopeStatsService
{
    Task<ScopeStatsResponse> ForBranchAsync(string branchKey, CancellationToken ct = default);
    Task<ScopeStatsResponse> ForInstanceAsync(string branchKey, string instanceKey, CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class ScopeStatsService(IJobStore jobs, IFilePairTaskStore tasks, IControlCheckStore checks) : IScopeStatsService
{
    public Task<ScopeStatsResponse> ForBranchAsync(string branchKey, CancellationToken ct = default) =>
        ComputeAsync(branchKey, instanceKey: null, ct);

    public Task<ScopeStatsResponse> ForInstanceAsync(string branchKey, string instanceKey, CancellationToken ct = default) =>
        ComputeAsync(branchKey, instanceKey, ct);

    private async Task<ScopeStatsResponse> ComputeAsync(string branchKey, string? instanceKey, CancellationToken ct)
    {
        // Load the scope's jobs once: gives both the job-status breakdown and the ids for the comparison counts.
        var scopeJobs = await jobs.ListAsync(
            new JobListQuery { BranchKey = branchKey, InstanceKey = instanceKey, Limit = int.MaxValue }, ct);

        int JobCount(JobStatus s) => scopeJobs.Count(j => j.Status == s);
        var jobStats = new JobStats(
            JobCount(JobStatus.Queued), JobCount(JobStatus.Running), JobCount(JobStatus.Paused),
            JobCount(JobStatus.Completed), JobCount(JobStatus.Failed));

        var taskCounts = scopeJobs.Count == 0
            ? new Dictionary<FilePairTaskStatus, int>()
            : await tasks.CountByStatusForJobsAsync(scopeJobs.Select(j => j.Id).ToList(), ct);
        int TaskCount(FilePairTaskStatus s) => taskCounts.TryGetValue(s, out var n) ? n : 0;
        var comparisonStats = new ComparisonStats(
            Waiting: TaskCount(FilePairTaskStatus.Queued),
            Running: TaskCount(FilePairTaskStatus.Running),
            Completed: TaskCount(FilePairTaskStatus.Completed),
            Failed: TaskCount(FilePairTaskStatus.Failed),
            Skipped: TaskCount(FilePairTaskStatus.Skipped));

        // Checks bound to this scope. A branch view includes its branch-scoped checks and the instance-scoped
        // checks beneath it; an instance view only its own. Global checks are server-wide, not per-scope.
        var all = await checks.ListAsync(ct);
        var scopeChecks = all.Where(c => instanceKey is null
                ? c.BranchKey == branchKey && c.ScopeKind is CheckScopeKind.Branch or CheckScopeKind.Instance
                : c.BranchKey == branchKey && c.InstanceKey == instanceKey && c.ScopeKind == CheckScopeKind.Instance)
            .ToList();

        int active = scopeChecks.Count(c => c.Enabled);
        int disabled = scopeChecks.Count(c => !c.Enabled);
        int completed = scopeChecks.Count(c => c.LastOutcome == CheckRunOutcome.Ok);
        int failed = scopeChecks.Count(c => c.LastOutcome is CheckRunOutcome.Failed or CheckRunOutcome.Warning);

        // Checks have no persistent "running" state (a run is transient), so Running is 0.
        var checkStats = new CheckStats(Active: active, Running: 0, Completed: completed, Failed: failed);
        // Automations are the umbrella over all automation types; control checks are the only type today, so a
        // disabled check maps to a "paused" automation.
        var automationStats = new AutomationStats(Active: active, Running: 0, Paused: disabled, Completed: completed, Failed: failed);

        return new ScopeStatsResponse(jobStats, checkStats, automationStats, comparisonStats);
    }
}
