using DiffPdf.Core.Models;

namespace DiffPdf.Persistence;

/// <summary>
/// Persists a new job and publishes its work command. The PostgreSQL
/// implementation does both in a single transaction (transactional outbox);
/// the in-memory dev implementation does them sequentially.
/// </summary>
public interface IJobSubmissionService
{
    Task SubmitAsync(ComparisonJob job, object command, CancellationToken ct = default);
}
