using DiffPdf.Core.Comparison;
using DiffPdf.Core.Models;
using DiffPdf.Core.Network;
using DiffPdf.Messaging.Messages;
using DiffPdf.Persistence;
using Microsoft.Extensions.Logging;

namespace DiffPdf.Messaging.Scheduling;

/// <summary>The comparison knobs a launched batch runs with (carried by a schedule or a run-now action).</summary>
public sealed record LaunchSpec(
    ComparisonOptions Options,
    BatchGate? Gate,
    string SearchPattern,
    bool Recursive,
    int MaxDegreeOfParallelism)
{
    public static LaunchSpec FromSchedule(ComparisonSchedule s) =>
        new(s.Options, s.Gate, s.SearchPattern, s.Recursive, s.MaxDegreeOfParallelism);
}

/// <summary>
/// Creates and starts a batch for a configured branch/instance in one step — the automation
/// equivalent of the (removed) manual <c>POST /batch</c> + <c>POST /jobs/{id}/start</c>. The job
/// is persisted as <see cref="JobStatus.Queued"/> and its run command published atomically
/// (transactional outbox on relational stores). Shared by the scheduler and the run-now endpoint.
/// </summary>
public interface IBatchLauncher
{
    /// <summary>
    /// Returns the new job id, or null when the run was skipped (missing/disabled scope, nothing to
    /// compare, unreachable base). The same pre-flight gate the readiness endpoint reports.
    /// </summary>
    Task<Guid?> LaunchAsync(string branchKey, string instanceKey, LaunchSpec spec, CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class BatchLauncher(
    IBranchStore branches,
    IInstanceStore instances,
    IInstanceStructureService structure,
    INetworkShareResolver shareResolver,
    IJobSubmissionService submission,
    ILogger<BatchLauncher> logger) : IBatchLauncher
{
    public async Task<Guid?> LaunchAsync(string branchKey, string instanceKey, LaunchSpec spec, CancellationToken ct = default)
    {
        var branch = await branches.GetByKeyAsync(branchKey, ct);
        if (branch is null || !branch.Enabled)
        {
            logger.LogWarning("Skipping run: branch '{Branch}' not found or disabled.", branchKey);
            return null;
        }

        var instance = await instances.GetByKeyAsync(branch.Id, instanceKey, ct);
        if (instance is null || !instance.Enabled)
        {
            logger.LogWarning("Skipping run: instance '{Branch}/{Instance}' not found or disabled.", branchKey, instanceKey);
            return null;
        }

        // Pre-flight gate, identical to the readiness endpoint: don't launch an empty batch.
        var report = await structure.InspectAsync(instance.BasePath, instance.CredentialProfile, ct: ct);
        if (!report.HasComparableInputs)
        {
            logger.LogInformation(
                "Skipping run for {Branch}/{Instance}: nothing to compare (old={Old}, new={New}, reachable={Reachable}).",
                branchKey, instanceKey, report.OldPdfCount, report.NewPdfCount, report.Reachable);
            return null;
        }

        ResolvedFolder baseResolved;
        try
        {
            baseResolved = shareResolver.Resolve(instance.BasePath, inlineCredentials: null, credentialProfile: instance.CredentialProfile);
        }
        catch (NetworkConfigurationException ex)
        {
            logger.LogWarning(ex, "Skipping run for {Branch}/{Instance}: {Message}", branchKey, instanceKey, ex.Message);
            return null;
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

        await submission.SubmitAsync(job, new RunBatchComparison(job.Id, branchKey, instanceKey), ct);
        logger.LogInformation("Batch {JobId} launched for {Branch}/{Instance}.", job.Id, branchKey, instanceKey);
        return job.Id;
    }
}
