using DiffPdf.Core.Models;
using DiffPdf.Persistence.SqlServer.Entities;
using Riok.Mapperly.Abstractions;

namespace DiffPdf.Persistence.SqlServer.Mapping;

/// <summary>Source-generated (Mapperly) mappings from EF entities to domain models.</summary>
[Mapper]
public sealed partial class EntityMapper
{
    public partial Branch ToDomain(BranchEntity entity);

    public partial ComparisonInstance ToDomain(InstanceEntity entity);

    [MapProperty(nameof(JobEntity.RequestJson), nameof(ComparisonJob.Request))]
    [MapProperty(nameof(JobEntity.ReportJson), nameof(ComparisonJob.Report))]
    // List-only denormalized verdict columns; the domain model derives these from Report.
    [MapperIgnoreSource(nameof(JobEntity.DifferingCount))]
    [MapperIgnoreSource(nameof(JobEntity.ErrorCount))]
    [MapperIgnoreSource(nameof(JobEntity.GatePassed))]
    public partial ComparisonJob ToDomain(JobEntity entity);

    [MapProperty(nameof(FilePairTaskEntity.ResultJson), nameof(FilePairTask.Result))]
    [MapperIgnoreSource(nameof(FilePairTaskEntity.ResultStatus))] // denormalized verdict (list filter only); domain derives it from Result
    public partial FilePairTask ToDomain(FilePairTaskEntity entity);

    [MapProperty(nameof(SubscriptionEntity.RecipientsJson), nameof(NotificationSubscription.Recipients))]
    [MapProperty(nameof(SubscriptionEntity.EventsJson), nameof(NotificationSubscription.Events))]
    [MapProperty(nameof(SubscriptionEntity.BranchKeysJson), nameof(NotificationSubscription.BranchKeys))]
    [MapProperty(nameof(SubscriptionEntity.InstanceKeysJson), nameof(NotificationSubscription.InstanceKeys))]
    public partial NotificationSubscription ToDomain(SubscriptionEntity entity);

    [MapperIgnoreSource(nameof(EmailSettingsEntity.Id))] // single-row settings; the domain model has no Id
    public partial EmailSettings ToDomain(EmailSettingsEntity entity);

    [MapProperty(nameof(AutomationEntity.StepsJson), nameof(Automation.Steps))]
    [MapProperty(nameof(AutomationEntity.EventTriggersJson), nameof(Automation.EventTriggers))]
    [MapProperty(nameof(AutomationEntity.EventsJson), nameof(Automation.Events))]
    public partial Automation ToDomain(AutomationEntity entity);

    [MapProperty(nameof(AutomationRunEntity.TriggerKind), nameof(AutomationRun.Trigger))]
    [MapProperty(nameof(AutomationRunEntity.StepResultsJson), nameof(AutomationRun.StepResults))]
    public partial AutomationRun ToDomain(AutomationRunEntity entity);

    public partial Trigger ToDomain(TriggerEntity entity);

    public partial TriggerRun ToDomain(TriggerRunEntity entity);

    public partial AuditEntry ToDomain(AuditLogEntity entity);

    [MapProperty(nameof(ScopeConfigurationEntity.TriggerConfigJson), nameof(ScopeConfiguration.TriggerConfig))]
    [MapProperty(nameof(ScopeConfigurationEntity.ComparisonOptionsJson), nameof(ScopeConfiguration.ComparisonOptions))]
    public partial ScopeConfiguration ToDomain(ScopeConfigurationEntity entity);

    // User-defined conversions Mapperly uses for the json columns.
    private static TriggerConfig MapTriggerConfig(string json) =>
        string.IsNullOrEmpty(json) ? new TriggerConfig() : DiffPdfJson.Deserialize<TriggerConfig>(json);

    private static ComparisonOptions MapComparisonOptions(string json) =>
        string.IsNullOrEmpty(json) ? new ComparisonOptions() : DiffPdfJson.Deserialize<ComparisonOptions>(json);

    private static BatchComparisonRequest MapRequest(string json) =>
        DiffPdfJson.Deserialize<BatchComparisonRequest>(json);

    private static BatchComparisonReport? MapReport(string? json) =>
        string.IsNullOrEmpty(json) ? null : DiffPdfJson.Deserialize<BatchComparisonReport>(json);

    private static FilePairResult? MapResult(string? json) =>
        string.IsNullOrEmpty(json) ? null : DiffPdfJson.Deserialize<FilePairResult>(json);

    private static IReadOnlyList<NotificationEvent> MapEvents(string json) =>
        string.IsNullOrEmpty(json) ? [] : DiffPdfJson.Deserialize<IReadOnlyList<NotificationEvent>>(json);

    private static IReadOnlyList<string> MapStringList(string json) =>
        string.IsNullOrEmpty(json) ? [] : DiffPdfJson.Deserialize<IReadOnlyList<string>>(json);

    private static IReadOnlyList<AutomationStep> MapSteps(string json) =>
        string.IsNullOrEmpty(json) ? [] : DiffPdfJson.Deserialize<IReadOnlyList<AutomationStep>>(json);

    private static IReadOnlyList<AutomationStepResult> MapStepResults(string json) =>
        string.IsNullOrEmpty(json) ? [] : DiffPdfJson.Deserialize<IReadOnlyList<AutomationStepResult>>(json);

    // Tolerate legacy/unknown `source` strings instead of throwing. Job rows that pre-dated the
    // `source` column were backfilled with an empty string (AddTriggers migration), and Mapperly's
    // generated Enum.Parse would crash the whole read path on them. Source is provenance metadata,
    // so an unknown value safely degrades to System. Used for ComparisonJob.Source and TriggerRun.Source.
    private static JobSource MapJobSource(string source) =>
        Enum.TryParse<JobSource>(source, ignoreCase: true, out var parsed) ? parsed : JobSource.System;
}
