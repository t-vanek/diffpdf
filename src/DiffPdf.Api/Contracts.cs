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

/// <summary>Probe a single folder (local, UNC or a <c>share:</c> alias) for reachability and PDF count.</summary>
public sealed record DiscoverFolderRequest
{
    public required string Folder { get; init; }

    /// <summary>Inline credentials (when allowed). Prefer <see cref="CredentialProfile"/>.</summary>
    public NetworkCredentials? Credentials { get; init; }

    /// <summary>Name of a configured credential profile.</summary>
    public string? CredentialProfile { get; init; }

    public string SearchPattern { get; init; } = "*.pdf";
    public bool Recursive { get; init; } = true;

    /// <summary>Maximum number of relative paths returned as a sample.</summary>
    public int SampleSize { get; init; } = 20;

    /// <summary>
    /// Optional branch key. When set (together with <see cref="InstanceKey"/>),
    /// the probe also verifies the scope exists. Leave both null to skip the scope check.
    /// </summary>
    public string? BranchKey { get; init; }

    /// <summary>Optional instance key under the branch; validated alongside it.</summary>
    public string? InstanceKey { get; init; }
}

/// <summary>Result of validating that a branch/instance scope exists.</summary>
public sealed record ScopeCheck(
    string BranchKey,
    bool BranchExists,
    string? BranchName,
    string InstanceKey,
    bool InstanceExists,
    string? InstanceName)
{
    /// <summary>True only when both the branch and the instance under it exist.</summary>
    public bool Ok => BranchExists && InstanceExists;
}

/// <summary>A folder probe enriched with an optional scope check and an overall readiness verdict.</summary>
public sealed record FolderDiscoveryResult(ScopeCheck? Scope, FolderDiscovery Folder)
{
    /// <summary>Whether the folder contains at least one matching PDF.</summary>
    public bool HasPdfs => Folder.FileCount > 0;

    /// <summary>
    /// True when the folder is reachable and contains PDFs, and — if a scope was
    /// requested — the branch and instance both exist. The "OK to submit a batch" signal.
    /// </summary>
    public bool Ready => Folder.Reachable && HasPdfs && (Scope?.Ok ?? true);
}

/// <summary>Dry-run a batch: how an old/new folder pair lines up, without comparing.</summary>
public sealed record PreviewPairingRequest
{
    public required string OldFolder { get; init; }
    public required string NewFolder { get; init; }

    public NetworkCredentials? OldFolderCredentials { get; init; }
    public NetworkCredentials? NewFolderCredentials { get; init; }
    public string? OldFolderCredentialProfile { get; init; }
    public string? NewFolderCredentialProfile { get; init; }

    public string SearchPattern { get; init; } = "*.pdf";
    public bool Recursive { get; init; } = true;
    public int SampleSize { get; init; } = 20;

    /// <summary>
    /// Optional branch key. When set (together with <see cref="InstanceKey"/>),
    /// the dry-run also verifies the scope exists. Leave both null to skip the scope check.
    /// </summary>
    public string? BranchKey { get; init; }

    /// <summary>Optional instance key under the branch; validated alongside it.</summary>
    public string? InstanceKey { get; init; }
}

/// <summary>A pairing dry-run enriched with an optional scope check and an overall readiness verdict.</summary>
public sealed record PairingPreviewResult(ScopeCheck? Scope, PairingPreview Pairing)
{
    /// <summary>Whether the folders yielded at least one PDF to pair.</summary>
    public bool HasPdfs => Pairing.Total > 0;

    /// <summary>
    /// True when the folders are reachable and contain PDFs, and — if a scope was
    /// requested — the branch and instance both exist. The "OK to submit a batch" signal.
    /// </summary>
    public bool Ready => Pairing.Reachable && HasPdfs && (Scope?.Ok ?? true);
}

/// <summary>Configured network: named shares and credential-profile names (never secrets).</summary>
public sealed record NetworkConfigSummary(IReadOnlyList<ShareInfo> Shares, IReadOnlyList<string> CredentialProfiles);

public sealed record CreateBranchRequest(string Key, string Name);

/// <summary>Create an instance under a branch. <paramref name="BasePath"/> holds the old/new/reports subfolders.</summary>
public sealed record CreateInstanceRequest(string Key, string Name, string BasePath, string? CredentialProfile = null);

/// <summary>Response after creating an instance: the record plus the result of provisioning its folder skeleton (null when skipped).</summary>
public sealed record CreatedInstanceResponse(ComparisonInstance Instance, InstanceStructureReport? Structure);

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
