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
    public partial ComparisonJob ToDomain(JobEntity entity);

    [MapProperty(nameof(FilePairTaskEntity.ResultJson), nameof(FilePairTask.Result))]
    public partial FilePairTask ToDomain(FilePairTaskEntity entity);

    [MapProperty(nameof(SubscriptionEntity.EventsJson), nameof(NotificationSubscription.Events))]
    public partial NotificationSubscription ToDomain(SubscriptionEntity entity);

    [MapProperty(nameof(ControlCheckEntity.ParametersJson), nameof(ControlCheck.Parameters))]
    [MapProperty(nameof(ControlCheckEntity.EventsJson), nameof(ControlCheck.Events))]
    public partial ControlCheck ToDomain(ControlCheckEntity entity);

    public partial ControlCheckRun ToDomain(ControlCheckRunEntity entity);

    [MapProperty(nameof(TriggerEntity.SpecJson), nameof(Trigger.Spec))]
    public partial Trigger ToDomain(TriggerEntity entity);

    public partial TriggerRun ToDomain(TriggerRunEntity entity);

    public partial AuditEntry ToDomain(AuditLogEntity entity);

    [MapProperty(nameof(ScopeConfigurationEntity.TriggerConfigJson), nameof(ScopeConfiguration.TriggerConfig))]
    [MapProperty(nameof(ScopeConfigurationEntity.ComparisonOptionsJson), nameof(ScopeConfiguration.ComparisonOptions))]
    public partial ScopeConfiguration ToDomain(ScopeConfigurationEntity entity);

    // User-defined conversions Mapperly uses for the json columns.
    private static TriggerSpec MapTriggerSpec(string json) =>
        string.IsNullOrEmpty(json) ? new TriggerSpec() : DiffPdfJson.Deserialize<TriggerSpec>(json);

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
        DiffPdfJson.Deserialize<IReadOnlyList<NotificationEvent>>(json);

    private static IReadOnlyDictionary<string, string> MapParameters(string json) =>
        string.IsNullOrEmpty(json) ? new Dictionary<string, string>() : DiffPdfJson.Deserialize<IReadOnlyDictionary<string, string>>(json);
}
