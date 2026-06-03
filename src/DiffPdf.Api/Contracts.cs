using DiffPdf.Core.Models;
using DiffPdf.Core.Network;

namespace DiffPdf.Api;

/// <summary>Request body for comparing a single old/new PDF pair.</summary>
public sealed record SingleComparisonRequest
{
    public required string OldPath { get; init; }
    public required string NewPath { get; init; }
    public ComparisonOptions Options { get; init; } = new();
}

/// <summary>
/// Request body for submitting a batch comparison. The old/new/reports folders are
/// derived server-side from the target instance's base path, so only the scope
/// (branch + instance) and tuning options are supplied.
/// </summary>
public sealed record SubmitBatchRequest
{
    /// <summary>Branch + instance to compare. The job reads <c>{base}/old</c> vs <c>{base}/new</c>.</summary>
    public required JobScope Scope { get; init; }

    /// <summary>Glob-style search pattern relative to each folder.</summary>
    public string SearchPattern { get; init; } = "*.pdf";

    public bool Recursive { get; init; } = true;

    public ComparisonOptions Options { get; init; } = new();

    /// <summary>Maximum number of file pairs compared concurrently. 0 = processor count.</summary>
    public int MaxDegreeOfParallelism { get; init; } = 0;

    /// <summary>Optional pass/fail criteria for CI gating. Null = no gating.</summary>
    public BatchGate? Gate { get; init; }
}

/// <summary>Configured network: named shares and credential-profile names (never secrets).</summary>
public sealed record NetworkConfigSummary(IReadOnlyList<ShareInfo> Shares, IReadOnlyList<string> CredentialProfiles);

public sealed record CreateBranchRequest(string Key, string Name);

/// <summary>Create an instance under a branch. <paramref name="BasePath"/> holds the old/new/reports subfolders.</summary>
public sealed record CreateInstanceRequest(string Key, string Name, string BasePath, string? CredentialProfile = null);

/// <summary>Response after creating an instance: the record plus the result of provisioning its folder skeleton (null when skipped).</summary>
public sealed record CreatedInstanceResponse(ComparisonInstance Instance, InstanceStructureReport? Structure);

/// <summary>
/// Pre-flight readiness of an instance for a batch. Combines the folder-skeleton
/// inspection (<see cref="Structure"/>: old/new/reports state + PDF counts, optionally
/// the file lists) with a dry-run pairing of old vs new. <see cref="Ready"/> mirrors the
/// batch gate (both input folders must hold at least one PDF).
/// </summary>
public sealed record InstanceReadiness(
    InstanceStructureReport Structure,
    int Matched,
    int OnlyInOld,
    int OnlyInNew,
    IReadOnlyList<string> SampleOnlyInOld,
    IReadOnlyList<string> SampleOnlyInNew,
    bool Ready,
    string? Error)
{
    /// <summary>Whether the instance base path was reachable.</summary>
    public bool Reachable => Structure.Reachable;

    /// <summary>Number of *.pdf files in the <c>old</c> input folder.</summary>
    public int OldPdfCount => Structure.OldPdfCount;

    /// <summary>Number of *.pdf files in the <c>new</c> input folder.</summary>
    public int NewPdfCount => Structure.NewPdfCount;
}

/// <summary>Per-file-pair task view.</summary>
public sealed record FilePairTaskSummary
{
    public required Guid Id { get; init; }
    public required string RelativePath { get; init; }
    public required string Status { get; init; }
    public int AttemptCount { get; init; }
    public string? ResultStatus { get; init; }
    public string? Error { get; init; }

    public static FilePairTaskSummary From(FilePairTask t) => new()
    {
        Id = t.Id,
        RelativePath = t.RelativePath,
        Status = t.Status.ToString(),
        AttemptCount = t.AttemptCount,
        ResultStatus = t.Result?.Status.ToString(),
        Error = t.Error,
    };
}

/// <summary>Lightweight job view returned by the API.</summary>
public sealed record JobSummary
{
    public required Guid Id { get; init; }
    public required string BranchKey { get; init; }
    public required string InstanceKey { get; init; }
    public required string Status { get; init; }
    public double Progress { get; init; }
    public int ProcessedCount { get; init; }
    public int TotalCount { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public string? Error { get; init; }

    public static JobSummary From(ComparisonJob job) => new()
    {
        Id = job.Id,
        BranchKey = job.BranchKey,
        InstanceKey = job.InstanceKey,
        Status = job.Status.ToString(),
        Progress = job.Progress,
        ProcessedCount = job.ProcessedCount,
        TotalCount = job.TotalCount,
        CreatedAt = job.CreatedAt,
        CompletedAt = job.CompletedAt,
        Error = job.Error,
    };
}

/// <summary>Result of triggering a single instance: the launch outcome and the new job id (when launched).</summary>
public sealed record TriggerResult(string Outcome, Guid? JobId, string? Detail);

/// <summary>Per-instance entry in a branch fan-out run.</summary>
public sealed record InstanceRunResult(string InstanceKey, string Outcome, Guid? JobId, string? Detail);

/// <summary>Result of triggering every enabled instance under a branch.</summary>
public sealed record BranchRunResult(string BranchKey, int Launched, int Skipped, IReadOnlyList<InstanceRunResult> Instances);
