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

    // User-defined conversions Mapperly uses for the json columns.
    private static BatchComparisonRequest MapRequest(string json) =>
        DiffPdfJson.Deserialize<BatchComparisonRequest>(json);

    private static BatchComparisonReport? MapReport(string? json) =>
        string.IsNullOrEmpty(json) ? null : DiffPdfJson.Deserialize<BatchComparisonReport>(json);

    private static FilePairResult? MapResult(string? json) =>
        string.IsNullOrEmpty(json) ? null : DiffPdfJson.Deserialize<FilePairResult>(json);
}
