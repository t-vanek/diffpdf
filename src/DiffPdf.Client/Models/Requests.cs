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
