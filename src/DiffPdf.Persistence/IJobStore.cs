using DiffPdf.Core.Models;

namespace DiffPdf.Persistence;

/// <summary>Persistence for batch comparison jobs.</summary>
public interface IJobStore
{
    Task<ComparisonJob> CreateAsync(BatchComparisonRequest request, CancellationToken ct = default);
    Task<ComparisonJob?> GetAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<ComparisonJob>> ListAsync(CancellationToken ct = default);
    Task UpdateAsync(ComparisonJob job, CancellationToken ct = default);
}
