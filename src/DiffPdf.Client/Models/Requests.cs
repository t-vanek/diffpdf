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

/// <summary>Compare a single old/new PDF pair synchronously.</summary>
public sealed record SingleComparisonRequest
{
    public required string OldPath { get; init; }
    public required string NewPath { get; init; }
    public ComparisonOptions Options { get; init; } = new();
}

/// <summary>Create a schedule under an instance; it runs the instance on its cron with the given options/gate.</summary>
public sealed record CreateScheduleRequest
{
    public required string Key { get; init; }
    public string? Name { get; init; }
    public required string Cron { get; init; }
    public ComparisonOptions Options { get; init; } = new();
    public BatchGate? Gate { get; init; }
    public string SearchPattern { get; init; } = "*.pdf";
    public bool Recursive { get; init; } = true;
    public int MaxDegreeOfParallelism { get; init; }
    public bool Enabled { get; init; } = true;
}

/// <summary>Update a schedule. <see cref="Version"/> guards against concurrent edits (409 on mismatch).</summary>
public sealed record UpdateScheduleRequest
{
    public string? Name { get; init; }
    public required string Cron { get; init; }
    public ComparisonOptions Options { get; init; } = new();
    public BatchGate? Gate { get; init; }
    public string SearchPattern { get; init; } = "*.pdf";
    public bool Recursive { get; init; } = true;
    public int MaxDegreeOfParallelism { get; init; }
    public bool Enabled { get; init; } = true;
    public required long Version { get; init; }
}

/// <summary>Create a notification subscription routing finished-batch events to a webhook or e-mail.</summary>
public sealed record CreateSubscriptionRequest
{
    public required string Channel { get; init; }
    public required string Target { get; init; }
    public required IReadOnlyList<NotificationEvent> Events { get; init; }
    public string? BranchKey { get; init; }
    public string? InstanceKey { get; init; }
    public bool Enabled { get; init; } = true;
}

/// <summary>Update a subscription. <see cref="Version"/> guards against concurrent edits (409 on mismatch).</summary>
public sealed record UpdateSubscriptionRequest
{
    public required string Channel { get; init; }
    public required string Target { get; init; }
    public required IReadOnlyList<NotificationEvent> Events { get; init; }
    public string? BranchKey { get; init; }
    public string? InstanceKey { get; init; }
    public bool Enabled { get; init; } = true;
    public required long Version { get; init; }
}
