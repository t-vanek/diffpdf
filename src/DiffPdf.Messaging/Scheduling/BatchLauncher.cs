using DiffPdf.Core.Comparison;
using DiffPdf.Core.Models;
using DiffPdf.Core.Network;
using DiffPdf.Messaging.Messages;
using DiffPdf.Persistence;
using Microsoft.Extensions.Logging;

namespace DiffPdf.Messaging.Scheduling;

/// <summary>The comparison knobs a launched batch runs with, plus its provenance (trigger + source).</summary>
public sealed record LaunchSpec(
    ComparisonOptions Options,
    BatchGate? Gate,
    string SearchPattern,
    bool Recursive,
    int MaxDegreeOfParallelism,
    Guid? TriggerId = null,
    JobSource Source = JobSource.System,
    int Priority = 0)
{
    /// <summary>Default knobs (default options, no gate, all *.pdf recursively) — used by the on-demand triggers.</summary>
    public static LaunchSpec Default { get; } = new(new ComparisonOptions(), null, "*.pdf", true, 0);

    /// <summary>
    /// Composes the launch knobs from a resolved <see cref="EffectiveConfiguration"/> — the effective comparer
    /// options plus the effective trigger config (search pattern, recursion, parallelism, gate). This is how
    /// the per-scope inheritance actually drives a run. <paramref name="priority"/> is the per-branch queue
    /// priority (0 = enqueue at back, 100 = run now / jump ahead).
    /// </summary>
    public static LaunchSpec FromEffective(EffectiveConfiguration eff, Guid? triggerId = null, JobSource source = JobSource.System, int priority = 0) =>
        new(eff.ComparisonOptions, eff.TriggerConfig.Gate, eff.TriggerConfig.SearchPattern,
            eff.TriggerConfig.Recursive, eff.TriggerConfig.MaxDegreeOfParallelism, triggerId, source, priority);
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

/// <inheritdoc />
public sealed class BatchLauncher(
    IBranchStore branches,
    IInstanceStore instances,
    IInstanceStructureService structure,
    INetworkShareResolver shareResolver,
    IJobSubmissionService submission,
    IJobStore jobs,
    ILogger<BatchLauncher> logger) : IBatchLauncher
{
    public async Task<LaunchResult> LaunchAsync(string branchKey, string instanceKey, LaunchSpec spec, bool enqueueOnly = false, CancellationToken ct = default)
    {
        var branch = await branches.GetByKeyAsync(branchKey, ct);
        if (branch is null || !branch.Enabled)
        {
            logger.LogWarning("Skipping launch: branch '{Branch}' not found or disabled.", branchKey);
            return new LaunchResult(LaunchOutcome.ScopeNotFound, Detail: $"Branch '{branchKey}' not found or disabled.");
        }

        var instance = await instances.GetByKeyAsync(branch.Id, instanceKey, ct);
        if (instance is null || !instance.Enabled)
        {
            logger.LogWarning("Skipping launch: instance '{Branch}/{Instance}' not found or disabled.", branchKey, instanceKey);
            return new LaunchResult(LaunchOutcome.ScopeNotFound, Detail: $"Instance '{branchKey}/{instanceKey}' not found or disabled.");
        }

        // Pre-flight gate, identical to the readiness endpoint: don't launch an empty batch.
        var report = await structure.InspectAsync(instance.BasePath, instance.CredentialProfile, ct: ct);
        if (!report.Reachable)
        {
            logger.LogWarning("Skipping launch for {Branch}/{Instance}: base path unreachable ({Error}).",
                branchKey, instanceKey, report.Error);
            return new LaunchResult(LaunchOutcome.Unreachable, Detail: report.Error ?? "Base path unreachable.");
        }
        if (!report.HasComparableInputs)
        {
            logger.LogInformation(
                "Skipping launch for {Branch}/{Instance}: nothing to compare (old={Old}, new={New}).",
                branchKey, instanceKey, report.OldPdfCount, report.NewPdfCount);
            return new LaunchResult(LaunchOutcome.NothingToCompare,
                Detail: $"Nothing to compare: old={report.OldPdfCount}, new={report.NewPdfCount}.");
        }

        ResolvedFolder baseResolved;
        try
        {
            baseResolved = shareResolver.Resolve(instance.BasePath, inlineCredentials: null, credentialProfile: instance.CredentialProfile);
        }
        catch (NetworkConfigurationException ex)
        {
            logger.LogWarning(ex, "Skipping launch for {Branch}/{Instance}: {Message}", branchKey, instanceKey, ex.Message);
            return new LaunchResult(LaunchOutcome.Unreachable, Detail: ex.Message);
        }

        string basePath = baseResolved.Path;
        var job = new ComparisonJob
        {
            Id = Guid.NewGuid(),
            // enqueueOnly: persist as Draft (pending in the branch queue); the dispatcher releases it.
            Status = enqueueOnly ? JobStatus.Draft : JobStatus.Queued,
            Priority = spec.Priority,
            Request = new BatchComparisonRequest
            {
                Scope = new JobScope(branchKey, instanceKey),
                OldFolder = InstanceFolders.Old(basePath),
                NewFolder = InstanceFolders.New(basePath),
                ReportsFolder = InstanceFolders.Reports(basePath),
                SearchPattern = spec.SearchPattern,
                Recursive = spec.Recursive,
                Options = spec.Options,
                MaxDegreeOfParallelism = spec.MaxDegreeOfParallelism,
                Gate = spec.Gate,
                OldFolderCredentials = baseResolved.Credentials,
                NewFolderCredentials = baseResolved.Credentials,
            },
            BranchId = branch.Id,
            InstanceId = instance.Id,
            TriggerId = spec.TriggerId,
            Source = spec.Source,
        };

        if (enqueueOnly)
        {
            // Pending in the branch queue — the dispatcher publishes RunBatchComparison when the branch is free.
            await jobs.CreateAsync(job, ct);
            logger.LogInformation("Batch {JobId} enqueued for {Branch}/{Instance} (priority {Priority}).", job.Id, branchKey, instanceKey, spec.Priority);
        }
        else
        {
            await submission.SubmitAsync(job, new RunBatchComparison(job.Id, branchKey, instanceKey), ct);
            logger.LogInformation("Batch {JobId} launched for {Branch}/{Instance}.", job.Id, branchKey, instanceKey);
        }
        return new LaunchResult(LaunchOutcome.Launched, job.Id);
    }
}
