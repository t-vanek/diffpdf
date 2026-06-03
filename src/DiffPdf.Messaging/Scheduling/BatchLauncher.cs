using DiffPdf.Core.Comparison;
using DiffPdf.Core.Models;
using DiffPdf.Core.Network;
using DiffPdf.Messaging.Messages;
using DiffPdf.Persistence;
using Microsoft.Extensions.Logging;

namespace DiffPdf.Messaging.Scheduling;

/// <summary>The comparison knobs a launched batch runs with (carried by a schedule, or defaults for a trigger).</summary>
public sealed record LaunchSpec(
    ComparisonOptions Options,
    BatchGate? Gate,
    string SearchPattern,
    bool Recursive,
    int MaxDegreeOfParallelism,
    Guid? ScheduleId = null)
{
    /// <summary>Default knobs (default options, no gate, all *.pdf recursively) — used by webhook / folder-watch triggers, which carry no schedule.</summary>
    public static LaunchSpec Default { get; } = new(new ComparisonOptions(), null, "*.pdf", true, 0);

    public static LaunchSpec FromSchedule(ComparisonSchedule s) =>
        new(s.Options, s.Gate, s.SearchPattern, s.Recursive, s.MaxDegreeOfParallelism, s.Id);
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
/// Creates and starts a batch for a configured branch/instance in one step — the automation
/// equivalent of the (removed) manual <c>POST /batch</c> + <c>POST /jobs/{id}/start</c>. The job
/// is persisted as <see cref="JobStatus.Queued"/> and its run command published atomically
/// (transactional outbox on relational stores). Used by the scheduler, the run-now endpoint,
/// the webhook trigger and folder-watch.
/// </summary>
public interface IBatchLauncher
{
    /// <summary>
    /// Launches a batch for the scope with the given <paramref name="spec"/>, after the same
    /// pre-flight readiness gate the readiness endpoint reports. The result carries the outcome
    /// and the new job id (when launched).
    /// </summary>
    Task<LaunchResult> LaunchAsync(string branchKey, string instanceKey, LaunchSpec spec, CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class BatchLauncher(
    IBranchStore branches,
    IInstanceStore instances,
    IInstanceStructureService structure,
    INetworkShareResolver shareResolver,
    IJobSubmissionService submission,
    IScheduleRunStore scheduleRuns,
    ILogger<BatchLauncher> logger) : IBatchLauncher
{
    public async Task<LaunchResult> LaunchAsync(string branchKey, string instanceKey, LaunchSpec spec, CancellationToken ct = default)
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
            Status = JobStatus.Queued,
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
        };

        // Record the schedule run BEFORE publishing the command, so the finish-time patch (by job id)
        // can never lose a race with a fast in-process / DB-local pipeline. No-op for non-schedule launches.
        if (spec.ScheduleId is { } scheduleId)
            await scheduleRuns.RecordStartAsync(scheduleId, job.Id, DateTimeOffset.UtcNow, ct);

        await submission.SubmitAsync(job, new RunBatchComparison(job.Id, branchKey, instanceKey), ct);
        logger.LogInformation("Batch {JobId} launched for {Branch}/{Instance}.", job.Id, branchKey, instanceKey);
        return new LaunchResult(LaunchOutcome.Launched, job.Id);
    }
}
