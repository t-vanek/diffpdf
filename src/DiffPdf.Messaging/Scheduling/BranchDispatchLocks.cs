using System.Collections.Concurrent;

namespace DiffPdf.Messaging.Scheduling;

/// <summary>
/// Process-wide per-branch mutex. Serializes the "is the branch free? release / claim the next job"
/// decision across the dispatcher, the completion handlers and the claim handler, so a branch can never
/// start two jobs at once in this process (the single-node target). Multi-replica would additionally need
/// a DB-level guard. Registered as a singleton.
/// </summary>
public sealed class BranchDispatchLocks
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();

    public SemaphoreSlim For(Guid branchId) => _locks.GetOrAdd(branchId, _ => new SemaphoreSlim(1, 1));

    /// <summary>Drops a deleted branch's lock so the map doesn't grow unbounded.</summary>
    public void Remove(Guid branchId)
    {
        if (_locks.TryRemove(branchId, out var sem))
            sem.Dispose();
    }
}
