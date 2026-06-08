using DiffPdf.Core.Models;

namespace DiffPdf.Application.Abstractions;

/// <summary>
/// Resumes a paused job and re-dispatches its still-pending file pairs (or finalizes if none remain).
/// Shared by the job resume endpoint and the branch-queue resume action so the two never drift.
/// </summary>
public interface IJobResumeService
{
    /// <summary>Returns the resumed job (null if it was not Paused) and how many pending pairs were re-dispatched.</summary>
    Task<(ComparisonJob? Job, int Redispatched)> ResumeAsync(Guid jobId, CancellationToken ct = default);
}
