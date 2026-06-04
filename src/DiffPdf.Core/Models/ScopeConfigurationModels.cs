namespace DiffPdf.Core.Models;

/// <summary>
/// The three configuration scope levels, from broadest to narrowest. A configuration defined at a
/// narrower level may either inherit from its parent or override it with its own custom payload.
/// </summary>
public enum ConfigScopeLevel
{
    /// <summary>Application-wide defaults. The root of every inheritance chain.</summary>
    Global,
    /// <summary>Per-branch configuration; inherits from <see cref="Global"/> unless custom.</summary>
    Branch,
    /// <summary>Per-instance configuration; inherits from its branch unless custom.</summary>
    Instance,
}

/// <summary>
/// Where a scope sources its configuration from. Applied independently to triggers and comparers.
/// Valid values depend on the level: a branch may pick <see cref="Global"/> or <see cref="Custom"/>;
/// an instance may pick <see cref="Branch"/> or <see cref="Custom"/>; the global level is always custom.
/// </summary>
public enum ConfigSource
{
    /// <summary>Use the global (application) configuration.</summary>
    Global,
    /// <summary>Use the branch's effective configuration (instance level only).</summary>
    Branch,
    /// <summary>Use this scope's own custom payload.</summary>
    Custom,
}

/// <summary>
/// The non-comparison launch knobs (search pattern, recursion, parallelism, gate), split out so they can be
/// configured per scope independently of the comparer (<see cref="ComparisonOptions"/>) chain. Composed with
/// an effective <see cref="ComparisonOptions"/> into the launch <c>LaunchSpec</c> at run time.
/// </summary>
public sealed record TriggerConfig
{
    public string SearchPattern { get; init; } = "*.pdf";
    public bool Recursive { get; init; } = true;
    public int MaxDegreeOfParallelism { get; init; }
    public BatchGate? Gate { get; init; }
}

/// <summary>
/// A scope's stored configuration. Exactly one row exists per scope target: one <see cref="ConfigScopeLevel.Global"/>
/// row, one per branch, one per instance. Triggers and comparers each carry an independent source selector
/// and custom payload. Effective values are computed by the resolver, never stored.
/// </summary>
public sealed record ScopeConfiguration
{
    public required Guid Id { get; init; }
    public required ConfigScopeLevel Level { get; init; }

    /// <summary>Branch target for <see cref="ConfigScopeLevel.Branch"/>/<see cref="ConfigScopeLevel.Instance"/> rows (null for global).</summary>
    public Guid? BranchId { get; init; }
    /// <summary>Instance target for <see cref="ConfigScopeLevel.Instance"/> rows (null otherwise).</summary>
    public Guid? InstanceId { get; init; }

    /// <summary>
    /// Source selector for triggers. Branch level: Global|Custom; Instance level: Branch|Custom;
    /// Global level: always Custom.
    /// </summary>
    public ConfigSource TriggerSource { get; init; } = ConfigSource.Global;

    /// <summary>This level's own trigger payload (used when <see cref="TriggerSource"/> is Custom, or for the global row).</summary>
    public TriggerConfig TriggerConfig { get; init; } = new();

    /// <summary>Source selector for comparers, independent of <see cref="TriggerSource"/>.</summary>
    public ConfigSource ComparerSource { get; init; } = ConfigSource.Global;

    /// <summary>This level's own comparer payload (used when <see cref="ComparerSource"/> is Custom, or for the global row).</summary>
    public ComparisonOptions ComparisonOptions { get; init; } = new();

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; init; }
    public long Version { get; init; } = 1;
}

/// <summary>
/// The server-resolved effective configuration for a scope: the effective trigger and comparer payloads,
/// plus which <see cref="ConfigScopeLevel"/> each one resolved from. The UI consumes this directly and
/// performs no inheritance logic of its own.
/// </summary>
public sealed record EffectiveConfiguration(
    TriggerConfig TriggerConfig,
    ConfigScopeLevel TriggerResolvedFrom,
    ComparisonOptions ComparisonOptions,
    ConfigScopeLevel ComparerResolvedFrom);
