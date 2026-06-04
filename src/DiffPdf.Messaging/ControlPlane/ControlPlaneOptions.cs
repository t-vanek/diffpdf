namespace DiffPdf.Messaging.ControlPlane;

/// <summary>Configures the control-plane runner: the tick cadence and the automatic provisioning of
/// the standard checks. Checks themselves are runtime resources in the DB.</summary>
public sealed class ControlPlaneOptions
{
    public const string SectionName = "ControlPlane";

    /// <summary>How often the runner evaluates which checks are due (seconds). Clamped to a sane minimum.</summary>
    public int TickSeconds { get; set; } = 20;

    /// <summary>Automatic creation of the standard checks (baseline + per-branch readiness).</summary>
    public AutoProvisionOptions AutoProvision { get; set; } = new();
}

/// <summary>
/// Defaults for the control checks created automatically by <c>IControlCheckProvisioner</c>: a per-branch
/// Readiness check on branch creation, plus the server-wide baseline (Health, Retention and — when a
/// ScopeSync root is configured — StructureSync) at startup. Every value has a sensible default, so the
/// feature works with no configuration; set <see cref="Enabled"/> to <c>false</c> to turn it off entirely.
/// </summary>
public sealed class AutoProvisionOptions
{
    /// <summary>Master switch for automatic check creation. When false, nothing is auto-provisioned.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Cadence of the per-branch Readiness check (seconds).</summary>
    public int ReadinessIntervalSeconds { get; set; } = 300;

    /// <summary>Cadence of the server-wide Health check (seconds).</summary>
    public int HealthIntervalSeconds { get; set; } = 60;

    /// <summary>Cron (5-field, UTC) of the server-wide Retention check. Default: daily at 03:00.</summary>
    public string RetentionCron { get; set; } = "0 3 * * *";

    /// <summary>Retention window in days handed to the Retention check.</summary>
    public int RetentionDays { get; set; } = 30;

    /// <summary>Cadence of the server-wide StructureSync check (seconds); only created when a ScopeSync root is set.</summary>
    public int StructureSyncIntervalSeconds { get; set; } = 900;
}
