namespace DiffPdf.Application.Abstractions;

// ─────────────────────────────────────────────────────────────────────────────
//  Print SOA contracts. The concrete IPrintBatchService implementation lives in
//  the proprietary DiffPdf.Tisk assembly (the only project that references the
//  D3Soft packages); everything else — the orchestration handler, the endpoint,
//  the tests — depends only on these abstractions.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Connection + credentials for one D3Soft print-SOA client (one configured print source).
/// <see cref="SoaManagerUri"/> is the per-environment SOA manager endpoint; <see cref="SoaClientId"/>
/// the SOA client name. Credentials are resolved from a DiffPdf credential profile, never inline.
/// </summary>
public sealed record PrintSoaConnection(
    string SoaManagerUri,
    string SoaClientId,
    string Login,
    string Password,
    string TextPattern = "Error");

/// <summary>Outcome of a single PDF print-validation pass (blank-page or text-pattern).</summary>
public sealed record PrintValidation(bool IsError, string Summary, IReadOnlyList<string> InvalidFiles);

/// <summary>Result of printing a fresh sample batch: the issued batch number + the SOA's PDF validations.</summary>
public sealed record PrintSampleResult(string BatchNumber, PrintValidation BlankPage, PrintValidation TextPattern)
{
    /// <summary>True when either validation flagged a problem.</summary>
    public bool HasValidationError => BlankPage.IsError || TextPattern.IsError;

    /// <summary>One-line human summary of both validations (CZ), suitable for surfacing on the operation.</summary>
    public string ValidationSummary => $"{BlankPage.Summary} {TextPattern.Summary}".Trim();
}

/// <summary>
/// Drives the D3Soft Tisk print SOA: print a fresh sample batch, find the previous batch, and
/// fetch a batch's PDFs to a local folder. The single proprietary seam — a null/absent
/// registration disables the "Vytisknout a porovnat" feature.
/// </summary>
public interface IPrintBatchService
{
    Task<PrintSampleResult> PrintSampleAsync(PrintSoaConnection conn, CancellationToken ct = default);
    Task<string?> FindPreviousBatchAsync(PrintSoaConnection conn, string currentBatch, CancellationToken ct = default);
    Task FetchBatchToFolderAsync(PrintSoaConnection conn, string batchNumber, string destinationFolder, CancellationToken ct = default);
}

// ─────────────────────────────────────────────────────────────────────────────
//  Configuration: which scopes have a print source, and how to reach its SOA.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Bound from the <c>"Tisk"</c> config section. Maps a <c>"branchKey/instanceKey"</c> scope to its
/// print source. A scope present here gains the "Vytisknout a porovnat" action; absent scopes don't.
/// </summary>
public sealed class TiskOptions
{
    public const string SectionName = "Tisk";

    /// <summary>Key = <c>"branchKey/instanceKey"</c> (case-insensitive). Value = that scope's print source.</summary>
    public Dictionary<string, PrintSource> Sources { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The canonical scope key used in <see cref="Sources"/>.</summary>
    public static string ScopeKey(string branchKey, string instanceKey) => $"{branchKey}/{instanceKey}";
}

/// <summary>A configured print source: a SOA endpoint + client, with credentials taken from a profile.</summary>
public sealed class PrintSource
{
    public required string SoaManagerUri { get; set; }
    public required string SoaClientId { get; set; }

    /// <summary>Name of a <c>Network:CredentialProfiles</c> entry supplying the SOA login/password.</summary>
    public string? CredentialProfile { get; set; }

    /// <summary>Text the print validation looks for to flag a bad print (default "Error").</summary>
    public string TextPattern { get; set; } = "Error";
}

/// <summary>Resolves a scope to its <see cref="PrintSoaConnection"/> (credentials from the profile).</summary>
public interface IPrintSourceResolver
{
    /// <summary>True when the scope has a configured print source.</summary>
    bool IsConfigured(string branchKey, string instanceKey);

    /// <summary>
    /// Resolves the connection for a scope. Throws <see cref="InvalidOperationException"/> when the
    /// scope has no print source or its credential profile is missing/incomplete.
    /// </summary>
    PrintSoaConnection Resolve(string branchKey, string instanceKey);

    /// <summary>All configured scope keys (<c>"branchKey/instanceKey"</c>) — lets the client show the action.</summary>
    IReadOnlyCollection<string> ConfiguredScopes { get; }
}

// ─────────────────────────────────────────────────────────────────────────────
//  Print-operation status — a lightweight, in-memory progress record so the UI
//  can follow print → fetch → launch before the comparison job appears.
// ─────────────────────────────────────────────────────────────────────────────

public enum PrintPhase
{
    Printing,
    FindingPrevious,
    Fetching,
    Launching,
    Completed,
    NoPreviousBatch,
    Failed,
}

/// <summary>Snapshot of a print-and-compare operation's progress.</summary>
public sealed record PrintOperationStatus(
    Guid OperationId,
    string BranchKey,
    string InstanceKey,
    PrintPhase Phase,
    string? NewBatch,
    string? PreviousBatch,
    Guid? JobId,
    string? ValidationSummary,
    string? Error,
    DateTimeOffset UpdatedAt)
{
    public bool IsTerminal => Phase is PrintPhase.Completed or PrintPhase.NoPreviousBatch or PrintPhase.Failed;
}

/// <summary>
/// In-memory registry of print-operation status. Best-effort/observability only — the durable
/// comparison job is the source of truth. Updates upsert, so a status lost to a restart is recreated.
/// </summary>
public interface IPrintOperationStore
{
    void Start(Guid operationId, string branchKey, string instanceKey);

    /// <summary>Advances the operation; non-null arguments overwrite, nulls keep the prior value.</summary>
    void SetPhase(
        Guid operationId,
        PrintPhase phase,
        string? newBatch = null,
        string? previousBatch = null,
        Guid? jobId = null,
        string? validationSummary = null,
        string? error = null);

    PrintOperationStatus? Get(Guid operationId);
}
