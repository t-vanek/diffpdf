using System.Collections.Concurrent;
using DiffPdf.Core.Models;

namespace DiffPdf.Persistence;

/// <summary>Thread-safe in-memory schedule-run store for single-instance / dev use. Does not survive restart.</summary>
public sealed class InMemoryScheduleRunStore : IScheduleRunStore
{
    private readonly ConcurrentDictionary<Guid, ScheduleRun> _byId = new();
    private readonly object _gate = new();

    public Task RecordStartAsync(Guid scheduleId, Guid jobId, DateTimeOffset startedAt, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_byId.Values.Any(r => r.JobId == jobId))
                return Task.CompletedTask; // idempotent

            var run = new ScheduleRun { Id = Guid.NewGuid(), ScheduleId = scheduleId, JobId = jobId, StartedAt = startedAt };
            _byId[run.Id] = run;
        }
        return Task.CompletedTask;
    }

    public Task CompleteByJobAsync(
        Guid jobId, ScheduleRunOutcome outcome, int differing, int errors, int filesWithContentErrors,
        bool passed, IReadOnlyList<string> gateViolations, string? error, DateTimeOffset completedAt,
        CancellationToken ct = default)
    {
        lock (_gate)
        {
            var existing = _byId.Values.FirstOrDefault(r => r.JobId == jobId);
            if (existing is null)
                return Task.CompletedTask; // non-schedule job — nothing to patch

            _byId[existing.Id] = existing with
            {
                Outcome = outcome,
                Differing = differing,
                Errors = errors,
                FilesWithContentErrors = filesWithContentErrors,
                Passed = passed,
                GateViolations = gateViolations,
                Error = error,
                CompletedAt = completedAt,
            };
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ScheduleRun>> ListByScheduleAsync(Guid scheduleId, int limit = 50, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ScheduleRun>>(
            _byId.Values.Where(r => r.ScheduleId == scheduleId)
                .OrderByDescending(r => r.StartedAt).Take(limit).ToList());
}
