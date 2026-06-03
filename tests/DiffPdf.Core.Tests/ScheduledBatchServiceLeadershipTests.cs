using DiffPdf.Core.Abstractions;
using DiffPdf.Messaging.Scheduling;
using DiffPdf.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiffPdf.Core.Tests;

public class ScheduledBatchServiceLeadershipTests
{
    private sealed class FakeLeaderElection(bool isLeader) : ILeaderElection
    {
        public Task<bool> TryAcquireAsync(string role, string owner, TimeSpan lease, CancellationToken ct = default) =>
            Task.FromResult(isLeader);

        public Task<LeaderLease?> GetAsync(string role, CancellationToken ct = default) =>
            Task.FromResult<LeaderLease?>(isLeader
                ? new LeaderLease("test", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.MaxValue)
                : null);
    }

    private sealed class StubWorkerId(string id) : IWorkerInstanceIdProvider
    {
        public string WorkerInstanceId { get; } = id;
    }

    // Fails the test loudly if the scheduler tries to do work (it must short-circuit when not leader).
    private sealed class ThrowingScopeFactory : IServiceScopeFactory
    {
        public IServiceScope CreateScope() =>
            throw new InvalidOperationException("A non-leader replica must not open a scope or launch.");
    }

    [Fact]
    public async Task NotLeader_Tick_DoesNoWork()
    {
        var svc = new ScheduledBatchService(
            new ThrowingScopeFactory(),
            new FakeLeaderElection(isLeader: false),
            new StubWorkerId("replica-2"),
            new AutomationHeartbeat(),
            NullLogger<ScheduledBatchService>.Instance);

        // No leadership → returns before resolving the store/launcher (ThrowingScopeFactory not hit).
        await svc.TickAsync(CancellationToken.None);
    }
}
