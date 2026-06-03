using DiffPdf.Core.Abstractions;
using DiffPdf.Persistence;

namespace DiffPdf.Core.Tests;

public class LeaderElectionTests
{
    [Fact]
    public async Task InMemory_AlwaysLeads()
    {
        var election = new InMemoryLeaderElection(new WorkerInstanceIdProvider());

        // Single process: it is the only candidate, so it always holds the lease — even for a
        // different owner id (there are no other replicas to contend with).
        Assert.True(await election.TryAcquireAsync(AutomationLeader.Role, "w1", AutomationLeader.Lease));
        Assert.True(await election.TryAcquireAsync(AutomationLeader.Role, "w2", AutomationLeader.Lease));
    }

    [Fact]
    public async Task InMemory_GetAsync_ReportsSelfAsLeaderForever()
    {
        var worker = new WorkerInstanceIdProvider();
        var election = new InMemoryLeaderElection(worker);

        // Read-only status: single process reports itself as the holder with a never-expiring lease.
        var lease = await election.GetAsync(AutomationLeader.Role);

        Assert.NotNull(lease);
        Assert.Equal(worker.WorkerInstanceId, lease!.Owner);
        Assert.True(lease.ExpiresAt > DateTimeOffset.UtcNow.AddYears(100));
    }

    [Fact]
    public void AutomationLease_ExceedsTickIntervals()
    {
        // The lease must outlive the scheduler (20 s) / folder-watch (default 15 s) tick so the
        // leader never lets it lapse between renewals.
        Assert.True(AutomationLeader.Lease > TimeSpan.FromSeconds(20));
    }
}
