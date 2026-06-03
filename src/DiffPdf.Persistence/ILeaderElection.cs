namespace DiffPdf.Persistence;

/// <summary>
/// Cross-replica leader election via a time-bounded database lease. One replica holds the lease
/// for a role and renews it each tick; the others stand by and take over once it expires. Used to
/// make the recurring scheduler and folder-watch fire once across API replicas instead of once per
/// replica. All time comparisons use the database clock, so replica clock skew is irrelevant.
/// </summary>
public interface ILeaderElection
{
    /// <summary>
    /// Acquires the lease for <paramref name="role"/> (stealing it when expired) or renews it when
    /// already held by <paramref name="owner"/>. Returns true when this owner holds the lease.
    /// </summary>
    Task<bool> TryAcquireAsync(string role, string owner, TimeSpan lease, CancellationToken ct = default);
}

/// <summary>Shared role + lease for the single automation leader (recurring scheduler + folder-watch).</summary>
public static class AutomationLeader
{
    public const string Role = "automation";

    /// <summary>
    /// Lease TTL. Must comfortably exceed the scheduler/watch tick intervals (≤20 s) so the leader
    /// never lets the lease lapse between ticks; on leader death a stand-by takes over within ~Lease.
    /// </summary>
    public static readonly TimeSpan Lease = TimeSpan.FromSeconds(60);
}

/// <summary>Single-process election: this is the only process, so it always leads (dev / in-memory fallback).</summary>
public sealed class InMemoryLeaderElection : ILeaderElection
{
    public Task<bool> TryAcquireAsync(string role, string owner, TimeSpan lease, CancellationToken ct = default) =>
        Task.FromResult(true);
}
