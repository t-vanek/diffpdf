namespace DiffPdf.Messaging.Automations;

/// <summary>Configures the automation engine: the tick cadence, run parallelism and the automatic
/// provisioning of the standard automations. Automations themselves are runtime resources in the DB.</summary>
public sealed class AutomationEngineOptions
{
    public const string SectionName = "Automation";

    /// <summary>Section name of the pre-engine control plane; bound as a fallback so existing
    /// deployments keep their configured cadences without an appsettings edit.</summary>
    public const string LegacySectionName = "ControlPlane";

    /// <summary>How often the engine evaluates which automations are due (seconds). Clamped to a sane minimum.</summary>
    public int TickSeconds { get; set; } = 20;

    /// <summary>How many automation runs may execute concurrently on the leading replica. A hung run
    /// occupies one slot and never blocks the tick itself.</summary>
    public int MaxConcurrentRuns { get; set; } = 4;

    /// <summary>Automatic creation of the standard automations (baseline + per-branch readiness).</summary>
    public AutoProvisionOptions AutoProvision { get; set; } = new();
}

/// <summary>
/// Defaults for the automations created automatically by <c>IAutomationProvisioner</c>: a per-branch
/// Readiness automation on branch creation, plus the server-wide baseline (Health, Retention and — when a
/// ScopeSync root is configured — StructureSync) at startup. Every value has a sensible default, so the
/// feature works with no configuration; set <see cref="Enabled"/> to <c>false</c> to turn it off entirely.
/// Property names match the legacy ControlPlane section so it binds 1:1.
/// </summary>
public sealed class AutoProvisionOptions
{
    /// <summary>Master switch for automatic creation. When false, nothing is auto-provisioned.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Cadence of the per-branch Readiness automation (seconds).</summary>
    public int ReadinessIntervalSeconds { get; set; } = 300;

    /// <summary>Cadence of the server-wide Health automation (seconds).</summary>
    public int HealthIntervalSeconds { get; set; } = 60;

    /// <summary>Cron (5-field, UTC) of the server-wide Retention automation. Default: daily at 03:00.</summary>
    public string RetentionCron { get; set; } = "0 3 * * *";

    /// <summary>Retention window in days handed to the (on-disk artifact) Retention step.</summary>
    public int RetentionDays { get; set; } = 30;

    /// <summary>Cron (5-field, UTC) of the server-wide DB-row retention automation. Default: daily at 04:00 (after artifact retention).</summary>
    public string DbRowRetentionCron { get; set; } = "0 4 * * *";

    /// <summary>
    /// DB-row retention window in days. Kept longer than <see cref="RetentionDays"/> so on-disk artifacts are
    /// pruned before their job rows; rows are in any case only deleted once their artifacts are gone.
    /// </summary>
    public int DbRowRetentionDays { get; set; } = 90;

    /// <summary>Cadence of the server-wide StructureSync automation (seconds); only created when a ScopeSync root is set.</summary>
    public int StructureSyncIntervalSeconds { get; set; } = 900;
}
