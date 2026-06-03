using DiffPdf.Core.Models;

namespace DiffPdf.Persistence;

/// <summary>
/// Persistence for schedule run history. A row is recorded (Pending) when a schedule launches a
/// job and patched — matched by job id — when the batch finishes. Patching a job that has no run
/// row (a trigger / folder-watch launch, which carries no schedule) is a no-op.
/// </summary>
public interface IScheduleRunStore
{
    /// <summary>Records a launched run as Pending. Idempotent on <paramref name="jobId"/> (unique).</summary>
    Task RecordStartAsync(Guid scheduleId, Guid jobId, DateTimeOffset startedAt, CancellationToken ct = default);

    /// <summary>Patches the run for <paramref name="jobId"/> with its final outcome; no-op when no run exists.</summary>
    Task CompleteByJobAsync(
        Guid jobId, ScheduleRunOutcome outcome, int differing, int errors, int filesWithContentErrors,
        bool passed, IReadOnlyList<string> gateViolations, string? error, DateTimeOffset completedAt,
        CancellationToken ct = default);

    /// <summary>Most recent runs for a schedule, newest first.</summary>
    Task<IReadOnlyList<ScheduleRun>> ListByScheduleAsync(Guid scheduleId, int limit = 50, CancellationToken ct = default);
}
