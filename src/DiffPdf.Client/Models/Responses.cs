using System.Text.Json.Serialization;

namespace DiffPdf.Client;

/// <summary>A branch (top-level scope).</summary>
public sealed record Branch
{
    public Guid Id { get; init; }
    public string Key { get; init; } = "";
    public string Name { get; init; } = "";
    public bool Enabled { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    public long Version { get; init; }
}

/// <summary>An instance under a branch (binds a customer to a base folder).</summary>
public sealed record Instance
{
    public Guid Id { get; init; }
    public Guid BranchId { get; init; }
    public string Key { get; init; } = "";
    public string Name { get; init; } = "";
    public string BasePath { get; init; } = "";
    public string? CredentialProfile { get; init; }
    public bool Enabled { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    public long Version { get; init; }
}

/// <summary>Response from creating an instance: the record plus the folder-provisioning result.</summary>
public sealed record CreatedInstanceResponse
{
    public required Instance Instance { get; init; }
    public InstanceStructureReport? Structure { get; init; }
}

/// <summary>State of one required subfolder after inspecting / ensuring it.</summary>
public sealed record StructureItem
{
    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
    public StructureItemState State { get; init; }
    public string? Detail { get; init; }
    /// <summary>Number of *.pdf (recursive) in old/new; null for reports or a missing folder.</summary>
    public int? PdfCount { get; init; }
    /// <summary>Complete relative paths; present only when files were requested.</summary>
    public IReadOnlyList<string>? Files { get; init; }
}

/// <summary>Result of inspecting / ensuring an instance's old/new/reports skeleton.</summary>
public sealed record InstanceStructureReport
{
    public bool Reachable { get; init; }
    public string BasePath { get; init; } = "";
    public IReadOnlyList<StructureItem> Items { get; init; } = [];
    public string? Error { get; init; }
    public bool Ok { get; init; }
}

/// <summary>
/// Pre-flight readiness of an instance for a batch: the folder-skeleton inspection
/// (<see cref="Structure"/>) combined with a dry-run pairing of old vs new.
/// </summary>
public sealed record InstanceReadiness
{
    /// <summary>Folder-skeleton state: old/new/reports presence + PDF counts (+ files when requested).</summary>
    public InstanceStructureReport Structure { get; init; } = new();
    public bool Reachable { get; init; }
    public int OldPdfCount { get; init; }
    public int NewPdfCount { get; init; }
    public int Matched { get; init; }
    public int OnlyInOld { get; init; }
    public int OnlyInNew { get; init; }
    public IReadOnlyList<string> SampleOnlyInOld { get; init; } = [];
    public IReadOnlyList<string> SampleOnlyInNew { get; init; } = [];
    public bool Ready { get; init; }
    public string? Error { get; init; }
}

/// <summary>Lightweight job view.</summary>
public sealed record JobSummary
{
    public Guid Id { get; init; }
    public string BranchKey { get; init; } = "";
    public string InstanceKey { get; init; } = "";
    public JobStatus Status { get; init; }
    public double Progress { get; init; }
    public int ProcessedCount { get; init; }
    public int TotalCount { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public string? Error { get; init; }
}

/// <summary>Result for a single matched (or unmatched) file pair.</summary>
public sealed record FilePairResult
{
    public string RelativePath { get; init; } = "";
    public FilePairStatus Status { get; init; }
    public double Similarity { get; init; }
    public int DifferingPages { get; init; }
    public int ContentErrorCount { get; init; }
    public string? HighlightedPdfPath { get; init; }
    public string? Error { get; init; }
}

/// <summary>Aggregate report for a whole batch run.</summary>
public sealed record BatchComparisonReport
{
    public string OldFolder { get; init; } = "";
    public string NewFolder { get; init; } = "";
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset CompletedAt { get; init; }
    public IReadOnlyList<FilePairResult> Files { get; init; } = [];
    public BatchGate? Gate { get; init; }
    public int Total { get; init; }
    public int Identical { get; init; }
    public int Differing { get; init; }
    public int OnlyInOld { get; init; }
    public int OnlyInNew { get; init; }
    public int Errors { get; init; }
    public int FilesWithContentErrors { get; init; }
    public IReadOnlyList<string> GateViolations { get; init; } = [];
    public bool Passed { get; init; }
}

/// <summary>CI-gate verdict (from <c>GET /jobs/{id}/result</c>).</summary>
public sealed record JobResult
{
    public bool Passed { get; init; }
    public bool Gated { get; init; }
    public IReadOnlyList<string> Violations { get; init; } = [];
    public JobResultSummary Summary { get; init; } = new();
}

public sealed record JobResultSummary
{
    public int Total { get; init; }
    public int Identical { get; init; }
    public int Differing { get; init; }
    public int OnlyInOld { get; init; }
    public int OnlyInNew { get; init; }
    public int Errors { get; init; }
    public int FilesWithContentErrors { get; init; }
}

/// <summary>A job's file-pair task view.</summary>
public sealed record FilePairTaskSummary
{
    public Guid Id { get; init; }
    public string RelativePath { get; init; } = "";
    public string Status { get; init; } = "";
    public int AttemptCount { get; init; }
    public string? ResultStatus { get; init; }
    public string? Error { get; init; }
}

/// <summary>Configured network: named shares + credential-profile names (never secrets).</summary>
public sealed record NetworkConfigSummary
{
    public IReadOnlyList<ShareInfo> Shares { get; init; } = [];
    public IReadOnlyList<string> CredentialProfiles { get; init; } = [];
}

public sealed record ShareInfo
{
    public string Name { get; init; } = "";
    public string? Root { get; init; }
    public string? LocalMountPath { get; init; }
    public bool RequiresCredentials { get; init; }
    public string? Description { get; init; }
}

/// <summary>OAuth2 token response from <c>POST /connect/token</c> (client-credentials).</summary>
public sealed record TokenResponse
{
    [JsonPropertyName("access_token")] public string AccessToken { get; init; } = "";
    [JsonPropertyName("token_type")] public string TokenType { get; init; } = "";
    [JsonPropertyName("expires_in")] public int ExpiresIn { get; init; }
}

/// <summary>Outcome of triggering a single instance.</summary>
public sealed record TriggerResult
{
    /// <summary>Launched / ScopeNotFound / NothingToCompare / Unreachable.</summary>
    public string Outcome { get; init; } = "";
    public Guid? JobId { get; init; }
    public string? Detail { get; init; }
}

/// <summary>Per-instance entry of a branch fan-out run.</summary>
public sealed record InstanceRunResult
{
    public string InstanceKey { get; init; } = "";
    public string Outcome { get; init; } = "";
    public Guid? JobId { get; init; }
    public string? Detail { get; init; }
}

/// <summary>Outcome of triggering every enabled instance under a branch.</summary>
public sealed record BranchRunResult
{
    public string BranchKey { get; init; } = "";
    public int Launched { get; init; }
    public int Skipped { get; init; }
    public IReadOnlyList<InstanceRunResult> Instances { get; init; } = [];
}
