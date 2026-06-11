namespace DiffPdf.Core.Models;

/// <summary>
/// The category an automation belongs to, derived from what it does to the system. Drives the grouped
/// gallery and list in the UI and the <c>category</c> filter in the API. A multi-step automation takes
/// the category of its dominant step (see <see cref="AutomationCatalog.CategoryOf(Automation)"/>).
/// </summary>
public enum AutomationCategory
{
    /// <summary>Watches state (server, resources, data, queues) and only notifies — no side effects.</summary>
    Monitoring,

    /// <summary>Does the product's own work — runs and steers comparisons.</summary>
    Operations,

    /// <summary>Keeps the system lean — prunes artifacts, rows, temp files and logs.</summary>
    Maintenance,

    /// <summary>Reconciles and moves data between the system and its surroundings (disk ↔ DB, shares, export).</summary>
    Synchronization,
}

/// <summary>The input type of an <see cref="AutomationParameterSpec"/>, so the UI can render a typed field.</summary>
public enum AutomationParameterType
{
    Int,
    Bool,
    String,
    Enum,
}

/// <summary>
/// Describes one parameter a step accepts: a stable <see cref="Key"/> (what lands in
/// <see cref="AutomationStep.Parameters"/>), a human <see cref="Label"/> and <see cref="Help"/>, its
/// <see cref="Type"/>, a <see cref="Default"/> and optional bounds / enum values. Lets the UI render a
/// labelled, validated field instead of free-text <c>key=value</c>.
/// </summary>
public sealed record AutomationParameterSpec(
    string Key,
    string Label,
    string Help,
    AutomationParameterType Type,
    string? Default = null,
    int? Min = null,
    int? Max = null,
    IReadOnlyList<string>? EnumValues = null);

/// <summary>
/// A named, editable starting point for an automation: category, human name, one-line purpose, icon,
/// recommended cadence/scope and pre-filled <see cref="Steps"/> + default notification events. Picking a
/// template creates an ordinary automation pre-filled from it; everything stays editable afterwards.
/// </summary>
public sealed record AutomationTemplate(
    string Key,
    AutomationCategory Category,
    string DisplayName,
    string Purpose,
    string Icon,
    AutomationScopeKind DefaultScope,
    IReadOnlyList<AutomationStep> Steps,
    string? RecommendedCron = null,
    int? RecommendedIntervalSeconds = null,
    IReadOnlyList<NotificationEvent>? DefaultEvents = null);

/// <summary>
/// Single source of truth for the human-facing metadata of automations: which category each step belongs
/// to, its display name, one-line purpose and parameter schema, plus the gallery of ready-to-edit
/// <see cref="Templates"/>. Pure data — referenced by the API (responses, <c>/templates</c> and
/// <c>/catalog</c> endpoints), the provisioner and (via the client) the desktop UI.
/// </summary>
public static class AutomationCatalog
{
    // Priority used to pick a multi-step automation's category: the "heaviest" side-effect wins.
    private static readonly AutomationCategory[] CategoryPriority =
        [AutomationCategory.Operations, AutomationCategory.Synchronization, AutomationCategory.Maintenance, AutomationCategory.Monitoring];

    /// <summary>The category each step type belongs to.</summary>
    public static AutomationCategory CategoryOf(AutomationStepType type) => type switch
    {
        AutomationStepType.Health => AutomationCategory.Monitoring,
        AutomationStepType.Readiness => AutomationCategory.Monitoring,
        AutomationStepType.SystemResource => AutomationCategory.Monitoring,
        AutomationStepType.QueueHealth => AutomationCategory.Monitoring,
        AutomationStepType.ComparisonFreshness => AutomationCategory.Monitoring,
        AutomationStepType.ScheduledComparison => AutomationCategory.Operations,
        AutomationStepType.Retention => AutomationCategory.Maintenance,
        AutomationStepType.DbRowRetention => AutomationCategory.Maintenance,
        AutomationStepType.StructureSync => AutomationCategory.Synchronization,
        _ => AutomationCategory.Maintenance,
    };

    /// <summary>The category of a whole automation: its dominant step (heaviest side-effect wins).</summary>
    public static AutomationCategory CategoryOf(Automation automation)
    {
        var present = automation.Steps.Select(s => CategoryOf(s.Type)).ToHashSet();
        foreach (var category in CategoryPriority)
            if (present.Contains(category))
                return category;
        return AutomationCategory.Monitoring;
    }

    /// <summary>Human display name for a step type (falls back to the enum name).</summary>
    public static string DisplayNameFor(AutomationStepType type) => type switch
    {
        AutomationStepType.Health => "Zdraví serveru",
        AutomationStepType.Readiness => "Připravenost dat",
        AutomationStepType.SystemResource => "Systémový zdroj",
        AutomationStepType.QueueHealth => "Fronta a zaseknuté joby",
        AutomationStepType.ComparisonFreshness => "Čerstvost porovnání",
        AutomationStepType.ScheduledComparison => "Plánované porovnání",
        AutomationStepType.Retention => "Úklid reportů",
        AutomationStepType.DbRowRetention => "Úklid databáze",
        AutomationStepType.StructureSync => "Synchronizace struktury",
        _ => type.ToString(),
    };

    /// <summary>One-line purpose for a step type.</summary>
    public static string PurposeFor(AutomationStepType type) => type switch
    {
        AutomationStepType.Health => "Hlídá databázi, renderer a zápis do úložiště; upozorní při výpadku.",
        AutomationStepType.Readiness => "Hlídá, že instance v rozsahu mají v old/ i new/ co porovnávat.",
        AutomationStepType.SystemResource => "Hlídá systémový zdroj (disk, CPU nebo RAM) proti prahům.",
        AutomationStepType.QueueHealth => "Hlídá hloubku fronty a joby běžící podezřele dlouho.",
        AutomationStepType.ComparisonFreshness => "Upozorní, když instance nevyprodukovala úspěšné porovnání déle než daná lhůta.",
        AutomationStepType.ScheduledComparison => "Pravidelně spustí porovnání pro každou zapnutou instanci v rozsahu.",
        AutomationStepType.Retention => "Maže diff-PDF a JSON reporty dokončených jobů starší než lhůta.",
        AutomationStepType.DbRowRetention => "Maže staré řádky z databáze, aby nerostla bez hranic.",
        AutomationStepType.StructureSync => "Srovná složky na disku se scope stromem (větve/instance) v databázi.",
        _ => string.Empty,
    };

    /// <summary>The purpose of a whole automation: that of its first step (the gallery/list summary line).</summary>
    public static string PurposeFor(Automation automation) =>
        automation.Steps.Count > 0 ? PurposeFor(automation.Steps[0].Type) : string.Empty;

    /// <summary>The parameter schema a step type accepts (empty when it takes none).</summary>
    public static IReadOnlyList<AutomationParameterSpec> ParametersFor(AutomationStepType type) => type switch
    {
        AutomationStepType.Retention =>
        [
            new("retentionDays", "Doba uchování (dny)", "Reporty starší než tolik dní se smažou.", AutomationParameterType.Int, "30", Min: 0),
            new("maxPerTick", "Max. mazání na běh", "Strop, aby jeden běh nezahltil disk.", AutomationParameterType.Int, "100", Min: 1),
        ],
        AutomationStepType.DbRowRetention =>
        [
            new("retentionDays", "Doba uchování (dny)", "Řádky starší než tolik dní se smažou.", AutomationParameterType.Int, "90", Min: 0),
            new("maxPerTick", "Max. mazání na běh", "Strop dávky jobů/úloh na jeden běh.", AutomationParameterType.Int, "1000", Min: 1),
        ],
        AutomationStepType.SystemResource =>
        [
            new("resource", "Zdroj", "Který systémový zdroj se hlídá.", AutomationParameterType.Enum, "disk", EnumValues: ["disk", "cpu", "ram"]),
            new("warnPercent", "Práh varování (%)", "Při překročení (CPU/RAM) nebo poklesu pod (disk) se hlásí varování.", AutomationParameterType.Int, "85", Min: 0, Max: 100),
            new("failPercent", "Kritický práh (%)", "Při překročení (CPU/RAM) nebo poklesu pod (volný disk) se hlásí selhání.", AutomationParameterType.Int, "95", Min: 0, Max: 100),
        ],
        AutomationStepType.QueueHealth =>
        [
            new("maxQueuedWarn", "Fronta — varování", "Počet čekajících jobů, od kterého se hlásí varování.", AutomationParameterType.Int, "50", Min: 0),
            new("maxQueuedFail", "Fronta — selhání", "Počet čekajících jobů, od kterého se hlásí selhání.", AutomationParameterType.Int, "200", Min: 0),
            new("stalledMinutes", "Zaseknutí (min)", "Běžící job starší než tolik minut se považuje za zaseknutý.", AutomationParameterType.Int, "30", Min: 1),
        ],
        AutomationStepType.ComparisonFreshness =>
        [
            new("warnAgeHours", "Stáří — varování (h)", "Bez úspěšného porovnání déle než tolik hodin → varování.", AutomationParameterType.Int, "24", Min: 1),
            new("failAgeHours", "Stáří — selhání (h)", "Bez úspěšného porovnání déle než tolik hodin → selhání.", AutomationParameterType.Int, "48", Min: 1),
        ],
        _ => [],
    };

    /// <summary>The ready-to-edit gallery, grouped in the UI by <see cref="AutomationTemplate.Category"/>.</summary>
    public static IReadOnlyList<AutomationTemplate> Templates { get; } =
    [
        // 🔍 Monitorovací
        new("health", AutomationCategory.Monitoring, "Zdraví serveru",
            PurposeFor(AutomationStepType.Health), "❤️", AutomationScopeKind.Global,
            [new AutomationStep { Type = AutomationStepType.Health }],
            RecommendedIntervalSeconds: 60,
            DefaultEvents: [NotificationEvent.HealthDegraded, NotificationEvent.AutomationRecovered]),

        new("readiness", AutomationCategory.Monitoring, "Připravenost dat",
            PurposeFor(AutomationStepType.Readiness), "✅", AutomationScopeKind.Branch,
            [new AutomationStep { Type = AutomationStepType.Readiness }],
            RecommendedIntervalSeconds: 300,
            DefaultEvents: [NotificationEvent.ReadinessFailed, NotificationEvent.AutomationRecovered]),

        new("disk-space", AutomationCategory.Monitoring, "Diskové úložiště",
            "Hlídá volné místo na svazku artefaktů; upozorní pod prahem.", "🖴", AutomationScopeKind.Global,
            [new AutomationStep
            {
                Type = AutomationStepType.SystemResource,
                Parameters = new Dictionary<string, string> { ["resource"] = "disk", ["warnPercent"] = "15", ["failPercent"] = "5" },
            }],
            RecommendedIntervalSeconds: 300,
            DefaultEvents: [NotificationEvent.HealthDegraded, NotificationEvent.AutomationRecovered]),

        new("cpu-load", AutomationCategory.Monitoring, "Vytížení CPU",
            "Hlídá trvale vysoké vytížení CPU nad prahem.", "🔥", AutomationScopeKind.Global,
            [new AutomationStep
            {
                Type = AutomationStepType.SystemResource,
                Parameters = new Dictionary<string, string> { ["resource"] = "cpu", ["warnPercent"] = "85", ["failPercent"] = "97" },
            }],
            RecommendedIntervalSeconds: 60,
            DefaultEvents: [NotificationEvent.HealthDegraded, NotificationEvent.AutomationRecovered]),

        new("memory", AutomationCategory.Monitoring, "Paměť RAM",
            "Hlídá využití paměti; upozorní nad prahem.", "🧠", AutomationScopeKind.Global,
            [new AutomationStep
            {
                Type = AutomationStepType.SystemResource,
                Parameters = new Dictionary<string, string> { ["resource"] = "ram", ["warnPercent"] = "85", ["failPercent"] = "95" },
            }],
            RecommendedIntervalSeconds: 60,
            DefaultEvents: [NotificationEvent.HealthDegraded, NotificationEvent.AutomationRecovered]),

        new("queue-health", AutomationCategory.Monitoring, "Fronta a zaseknuté joby",
            PurposeFor(AutomationStepType.QueueHealth), "🚦", AutomationScopeKind.Global,
            [new AutomationStep { Type = AutomationStepType.QueueHealth }],
            RecommendedIntervalSeconds: 120,
            DefaultEvents: [NotificationEvent.JobStalled, NotificationEvent.AutomationRecovered]),

        new("comparison-freshness", AutomationCategory.Monitoring, "Čerstvost porovnání",
            PurposeFor(AutomationStepType.ComparisonFreshness), "⏱️", AutomationScopeKind.Branch,
            [new AutomationStep
            {
                Type = AutomationStepType.ComparisonFreshness,
                Parameters = new Dictionary<string, string> { ["warnAgeHours"] = "24", ["failAgeHours"] = "48" },
            }],
            RecommendedCron: "0 * * * *",
            DefaultEvents: [NotificationEvent.AutomationRecovered]),

        // ⚙️ Provozní
        new("scheduled-comparison", AutomationCategory.Operations, "Plánované porovnání",
            PurposeFor(AutomationStepType.ScheduledComparison), "🔁", AutomationScopeKind.Instance,
            [new AutomationStep { Type = AutomationStepType.ScheduledComparison }],
            RecommendedCron: "0 2 * * *",
            DefaultEvents: [NotificationEvent.CompletedWithErrors, NotificationEvent.Failed]),

        // 🧹 Údržbové
        new("retention", AutomationCategory.Maintenance, "Úklid reportů",
            PurposeFor(AutomationStepType.Retention), "🗂️", AutomationScopeKind.Global,
            [new AutomationStep
            {
                Type = AutomationStepType.Retention,
                Parameters = new Dictionary<string, string> { ["retentionDays"] = "30" },
            }],
            RecommendedCron: "0 3 * * *"),

        new("db-row-retention", AutomationCategory.Maintenance, "Úklid databáze",
            PurposeFor(AutomationStepType.DbRowRetention), "🗄️", AutomationScopeKind.Global,
            [new AutomationStep
            {
                Type = AutomationStepType.DbRowRetention,
                Parameters = new Dictionary<string, string> { ["retentionDays"] = "90" },
            }],
            RecommendedCron: "30 3 * * *"),

        // 🔗 Synchronizační
        new("structure-sync", AutomationCategory.Synchronization, "Synchronizace struktury",
            PurposeFor(AutomationStepType.StructureSync), "🔗", AutomationScopeKind.Global,
            [new AutomationStep { Type = AutomationStepType.StructureSync }],
            RecommendedIntervalSeconds: 300,
            DefaultEvents: [NotificationEvent.StructureDrift, NotificationEvent.AutomationRecovered]),
    ];
}
