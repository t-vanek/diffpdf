using System.Collections.Concurrent;
using DiffPdf.Core.Models;

namespace DiffPdf.Persistence;

/// <summary>Thread-safe in-memory trigger-run history for dev/test.</summary>
public sealed class InMemoryTriggerRunStore : ITriggerRunStore
{
    private readonly ConcurrentDictionary<Guid, TriggerRun> _byId = new();

    public Task AddAsync(TriggerRun run, CancellationToken ct = default)
    {
        _byId[run.Id] = run;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<TriggerRun>> ListByTriggerAsync(Guid triggerId, int limit = 50, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<TriggerRun>>(
            _byId.Values.Where(r => r.TriggerId == triggerId).OrderByDescending(r => r.StartedAt).Take(limit).ToList());

    public Task<TriggerRun?> GetByIdempotencyKeyAsync(Guid triggerId, string idempotencyKey, CancellationToken ct = default) =>
        Task.FromResult(_byId.Values.FirstOrDefault(r => r.TriggerId == triggerId && r.IdempotencyKey == idempotencyKey));
}
