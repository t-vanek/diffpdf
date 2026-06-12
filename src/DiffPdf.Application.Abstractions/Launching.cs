using DiffPdf.Core.Comparison;
using DiffPdf.Core.Models;

namespace DiffPdf.Application.Abstractions;

/// <summary>The comparison knobs a launched batch runs with, plus its provenance (trigger + source).</summary>
public sealed record LaunchSpec(
    ComparisonOptions Options,
    BatchGate? Gate,
    string SearchPattern,
    bool Recursive,
    int MaxDegreeOfParallelism,
    Guid? TriggerId = null,
    JobSource Source = JobSource.System,
    int Priority = 0,
    Guid? SourceAutomationId = null)
{
    /// <summary>Default knobs (default options, no gate, all *.pdf recursively) — used by the on-demand triggers.</summary>
    public static LaunchSpec Default { get; } = new(new ComparisonOptions(), null, "*.pdf", true, 0);

    /// <summary>
    /// Composes the launch knobs from a resolved <see cref="EffectiveConfiguration"/> — the effective comparer
    /// options plus the effective trigger config (search pattern, recursion, parallelism, gate). This is how
    /// the per-scope inheritance actually drives a run. <paramref name="priority"/> is the per-branch queue
    /// priority (0 = enqueue at back, 100 = run now / jump ahead).
    /// </summary>
    public static LaunchSpec FromEffective(EffectiveConfiguration eff, Guid? triggerId = null, JobSource source = JobSource.System, int priority = 0, Guid? sourceAutomationId = null) =>
        new(eff.ComparisonOptions, eff.TriggerConfig.Gate, eff.TriggerConfig.SearchPattern,
            eff.TriggerConfig.Recursive, eff.TriggerConfig.MaxDegreeOfParallelism, triggerId, source, priority, sourceAutomationId);
}

/// <summary>Why a launch did or did not happen.</summary>
public enum LaunchOutcome
{
    /// <summary>A batch was created and queued.</summary>
    Launched,

    /// <summary>The branch or instance does not exist or is disabled.</summary>
    ScopeNotFound,

    /// <summary>The base path was reachable but had nothing to compare (empty old/new).</summary>
    NothingToCompare,

    /// <summary>The base path could not be resolved/reached.</summary>
    Unreachable,
}

/// <summary>Result of an automated launch attempt.</summary>
public sealed record LaunchResult(LaunchOutcome Outcome, Guid? JobId = null, string? Detail = null)
{
    public bool Launched => Outcome == LaunchOutcome.Launched;
}

/// <summary>
/// Creates and starts a batch for a configured branch/instance in one step. The job is persisted as
/// <see cref="JobStatus.Queued"/> and its run command published atomically (transactional outbox on
/// relational stores). Used by the on-demand triggers (single instance and branch fan-out).
/// </summary>
public interface IBatchLauncher
{
    /// <summary>
    /// Launches a batch for the scope with the given <paramref name="spec"/>, after the same
    /// pre-flight readiness gate the readiness endpoint reports. The result carries the outcome
    /// and the new job id (when launched). When <paramref name="enqueueOnly"/> is true the job is
    /// persisted as <see cref="JobStatus.Draft"/> (pending in the branch queue) without publishing its
    /// run command — the branch queue dispatcher releases it later; otherwise it is queued + dispatched now.
    /// </summary>
    Task<LaunchResult> LaunchAsync(string branchKey, string instanceKey, LaunchSpec spec, bool enqueueOnly = false, CancellationToken ct = default);
}
