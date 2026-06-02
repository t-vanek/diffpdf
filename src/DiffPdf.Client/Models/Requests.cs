namespace DiffPdf.Client;

/// <summary>Branch + instance a job belongs to.</summary>
public sealed record JobScope(string BranchKey, string InstanceKey);

/// <summary>Create a branch (top-level scope, e.g. "Alfa").</summary>
public sealed record CreateBranchRequest(string Key, string Name);

/// <summary>
/// Create an instance under a branch. <paramref name="BasePath"/> (local / UNC /
/// <c>share:</c> alias) holds the old/new/reports subfolders.
/// </summary>
public sealed record CreateInstanceRequest(string Key, string Name, string BasePath, string? CredentialProfile = null);

/// <summary>Submit a batch comparison; the old/new/reports folders come from the instance.</summary>
public sealed record SubmitBatchRequest
{
    public required JobScope Scope { get; init; }
    public string SearchPattern { get; init; } = "*.pdf";
    public bool Recursive { get; init; } = true;
    public ComparisonOptions Options { get; init; } = new();
    /// <summary>Max file pairs compared concurrently; 0 = processor count.</summary>
    public int MaxDegreeOfParallelism { get; init; }
    public BatchGate? Gate { get; init; }
}

/// <summary>Compare a single old/new PDF pair synchronously.</summary>
public sealed record SingleComparisonRequest
{
    public required string OldPath { get; init; }
    public required string NewPath { get; init; }
    public ComparisonOptions Options { get; init; } = new();
}

/// <summary>Probe a single folder for reachability + PDF count, optionally validating a scope.</summary>
public sealed record DiscoverFolderRequest
{
    public required string Folder { get; init; }
    public NetworkCredentials? Credentials { get; init; }
    public string? CredentialProfile { get; init; }
    public string SearchPattern { get; init; } = "*.pdf";
    public bool Recursive { get; init; } = true;
    public int SampleSize { get; init; } = 20;
    public string? BranchKey { get; init; }
    public string? InstanceKey { get; init; }
}

/// <summary>Dry-run an old/new folder pairing, optionally validating a scope.</summary>
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
    public string? BranchKey { get; init; }
    public string? InstanceKey { get; init; }
}
