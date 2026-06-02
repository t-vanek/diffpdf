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
    /// Optional business-instance key. When set (together with <see cref="ProjectKey"/>),
    /// the probe also verifies the scope exists. Leave both null to skip the scope check.
    /// </summary>
    public string? BusinessInstanceKey { get; init; }

    /// <summary>Optional project key under the business instance; validated alongside it.</summary>
    public string? ProjectKey { get; init; }
}

/// <summary>Result of validating that a business-instance/project scope exists.</summary>
public sealed record ScopeCheck(
    string BusinessInstanceKey,
    bool BusinessInstanceExists,
    string? BusinessInstanceName,
    string ProjectKey,
    bool ProjectExists,
    string? ProjectName)
{
    /// <summary>True only when both the instance and the project under it exist.</summary>
    public bool Ok => BusinessInstanceExists && ProjectExists;
}

/// <summary>A folder probe enriched with an optional scope check and an overall readiness verdict.</summary>
public sealed record FolderDiscoveryResult(ScopeCheck? Scope, FolderDiscovery Folder)
{
    /// <summary>Whether the folder contains at least one matching PDF.</summary>
    public bool HasPdfs => Folder.FileCount > 0;

    /// <summary>
    /// True when the folder is reachable and contains PDFs, and — if a scope was
    /// requested — the instance and project both exist. The "OK to submit a batch" signal.
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
}

/// <summary>Configured network: named shares and credential-profile names (never secrets).</summary>
public sealed record NetworkConfigSummary(IReadOnlyList<ShareInfo> Shares, IReadOnlyList<string> CredentialProfiles);

public sealed record CreateBusinessInstanceRequest(string Key, string Name);

public sealed record CreateProjectRequest(string Key, string Name);

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
    public required string BusinessInstanceKey { get; init; }
    public required string ProjectKey { get; init; }
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
        BusinessInstanceKey = job.BusinessInstanceKey,
        ProjectKey = job.ProjectKey,
        Status = job.Status.ToString(),
        Progress = job.Progress,
        ProcessedCount = job.ProcessedCount,
        TotalCount = job.TotalCount,
        CreatedAt = job.CreatedAt,
        CompletedAt = job.CompletedAt,
        Error = job.Error,
    };
}
