namespace DiffPdf.Client;

/// <summary>Lifecycle state of a batch comparison job.</summary>
public enum JobStatus
{
    Draft,
    Queued,
    Running,
    Paused,
    Completed,
    Failed,
    Cancelled,
}

/// <summary>Classification of a single file pair within a batch.</summary>
public enum FilePairStatus
{
    Identical,
    Differs,
    OnlyInOld,
    OnlyInNew,
    Error,
}

/// <summary>State of one required subfolder (old/new/reports) of an instance.</summary>
public enum StructureItemState
{
    Present,
    Missing,
    WrongType,
    Created,
    Repaired,
}

/// <summary>Which comparison strategy (or strategies) to run.</summary>
[Flags]
public enum ComparisonMode
{
    None = 0,
    Text = 1,
    Visual = 2,
    Both = Text | Visual,
}

/// <summary>Engine strictness presets, from pixel-perfect to forgiving.</summary>
public enum Strictness
{
    Exact,
    Strict,
    Balanced,
    Lenient,
}

/// <summary>Layout of the highlighted diff PDF.</summary>
public enum HighlightLayout
{
    SideBySide,
    Single,
}

/// <summary>How the highlighted diff PDF is rendered.</summary>
public enum HighlightStyle
{
    Raster,
    VectorOverlay,
}

/// <summary>Renderer backend for the visual comparison.</summary>
public enum RendererBackend
{
    Ghostscript,
    Pdfium,
}

/// <summary>Unit in which an ignore-region rectangle is expressed.</summary>
public enum IgnoreUnit
{
    Fraction,
    Points,
}

/// <summary>The kind of event a notification subscription can fire on (batch outcome or automation result).</summary>
public enum NotificationEvent
{
    Completed,
    GateViolated,
    Failed,
    ReadinessFailed,
    HealthDegraded,
    StructureDrift,
    AutomationRecovered,
    JobStalled,
    CompletedWithErrors,
    AutomationFailing,
}

/// <summary>What one automation step does.</summary>
public enum AutomationStepType
{
    Readiness,
    Health,
    StructureSync,
    Retention,
    DbRowRetention,
    ScheduledComparison,
}

/// <summary>How widely an automation applies.</summary>
public enum AutomationScopeKind
{
    Global,
    Branch,
    Instance,
}

/// <summary>Outcome of one automation run (or one step).</summary>
public enum AutomationRunOutcome
{
    Ok,
    Warning,
    Failed,
}

/// <summary>What caused an automation run to start.</summary>
public enum AutomationTriggerKind
{
    Schedule,
    Event,
    Manual,
}

/// <summary>The kind of action a trigger performs.</summary>
public enum TriggerActionType
{
    RunComparison,
}

/// <summary>Lifecycle state of a trigger.</summary>
public enum TriggerStatus
{
    Active,
    Inactive,
    Disabled,
    Running,
    Pending,
    Error,
    Deleted,
}

/// <summary>Where a run / batch job was launched from.</summary>
public enum JobSource
{
    Manager,
    RestApi,
    System,
    Scheduler,
}

/// <summary>The scope level a configuration applies to (Global → Branch → Instance).</summary>
public enum ConfigScopeLevel
{
    Global,
    Branch,
    Instance,
}

/// <summary>
/// Where a scope sources its configuration from. A branch picks Global or Custom; an instance picks
/// Branch or Custom; the global level is always Custom.
/// </summary>
public enum ConfigSource
{
    Global,
    Branch,
    Custom,
}

/// <summary>A run-queue control action for an instance or a whole branch.</summary>
public enum QueueAction
{
    Enqueue,
    Run,
    Pause,
    Resume,
    Stop,
}

/// <summary>An instance's position in its branch's sequential queue (Idle = nothing pending/active).</summary>
public enum InstanceQueueStatus
{
    Idle,
    Pending,
    Queued,
    Running,
    Paused,
}
