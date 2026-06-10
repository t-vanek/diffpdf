using DiffPdf.Core.Models;
using DiffPdf.Persistence;

namespace DiffPdf.Application.Stats;

/// <summary>Job counts by status for one scope.</summary>
public sealed record JobStats(int Queued, int Running, int Paused, int Completed, int Failed);

/// <summary>Check-style counts for one scope, derived from the scope's automations (kept for response-shape
/// compatibility with the Checks panel; Running counts automations with a run claim in flight).</summary>
public sealed record CheckStats(int Active, int Running, int Completed, int Failed);

/// <summary>Automation counts for one scope.</summary>
public sealed record AutomationStats(int Active, int Running, int Paused, int Completed, int Failed);

/// <summary>Comparison (file-pair) counts by status for one scope. Paused/Stopped are not modelled, so they are omitted.</summary>
public sealed record ComparisonStats(int Waiting, int Running, int Completed, int Failed, int Skipped);

/// <summary>Aggregated statistics for a single branch or instance scope (jobs, checks, automations, comparisons).</summary>
public sealed record ScopeStatsResponse(JobStats Jobs, CheckStats Checks, AutomationStats Automations, ComparisonStats Comparisons);

/// <summary>
/// Computes the per-branch / per-instance statistics shown in the branch and instance detail views:
/// jobs, checks, automations and comparisons. Aggregates from the existing stores. The scope's jobs are
/// loaded once — that single list yields both the job-status breakdown and the job ids used to count the
/// comparison (file-pair) tasks by status. Automations bound to the scope are filtered from the full list.
/// </summary>
public interface IScopeStatsService
{
    Task<ScopeStatsResponse> ForBranchAsync(string branchKey, CancellationToken ct = default);
    Task<ScopeStatsResponse> ForInstanceAsync(string branchKey, string instanceKey, CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class ScopeStatsService(IJobStore jobs, IFilePairTaskStore tasks, IAutomationStore automations) : IScopeStatsService
{
    public Task<ScopeStatsResponse> ForBranchAsync(string branchKey, CancellationToken ct = default) =>
        ComputeAsync(branchKey, instanceKey: null, ct);

    public Task<ScopeStatsResponse> ForInstanceAsync(string branchKey, string instanceKey, CancellationToken ct = default) =>
        ComputeAsync(branchKey, instanceKey, ct);

    private async Task<ScopeStatsResponse> ComputeAsync(string branchKey, string? instanceKey, CancellationToken ct)
    {
        // Load the scope's jobs once via the lightweight summary projection — scalar columns + status only, with
        // NO per-job request/report JSON hydration (which ListAsync does, and which is pure waste here: a scope
        // with thousands of historical jobs would deserialize every blob just to be counted). Yields both the
        // job-status breakdown and the ids used to count the comparison (file-pair) tasks by status.
        var scopeJobs = await jobs.ListSummariesAsync(
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

        // Automations bound to this scope. A branch view includes its branch-scoped automations and the
        // instance-scoped ones beneath it; an instance view only its own. Global automations are
        // server-wide, not per-scope.
        var all = await automations.ListAsync(ct);
        var scoped = all.Where(a => instanceKey is null
                ? a.BranchKey == branchKey && a.ScopeKind is AutomationScopeKind.Branch or AutomationScopeKind.Instance
                : a.BranchKey == branchKey && a.InstanceKey == instanceKey && a.ScopeKind == AutomationScopeKind.Instance)
            .ToList();

        int active = scoped.Count(a => a.Enabled);
        int disabled = scoped.Count(a => !a.Enabled);
        int running = scoped.Count(a => a.RunningSince is not null);
        int completed = scoped.Count(a => a.LastOutcome == AutomationRunOutcome.Ok);
        int failed = scoped.Count(a => a.LastOutcome is AutomationRunOutcome.Failed or AutomationRunOutcome.Warning);

        var checkStats = new CheckStats(Active: active, Running: running, Completed: completed, Failed: failed);
        var automationStats = new AutomationStats(Active: active, Running: running, Paused: disabled, Completed: completed, Failed: failed);

        return new ScopeStatsResponse(jobStats, checkStats, automationStats, comparisonStats);
    }
}
