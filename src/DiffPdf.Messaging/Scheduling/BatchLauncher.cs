using DiffPdf.Application.Abstractions;
using DiffPdf.Core.Comparison;
using DiffPdf.Core.Models;
using DiffPdf.Core.Network;
using DiffPdf.Messaging.Messages;
using DiffPdf.Persistence;
using Microsoft.Extensions.Logging;

namespace DiffPdf.Messaging.Scheduling;

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
            SourceAutomationId = spec.SourceAutomationId,
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
