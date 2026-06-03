using DiffPdf.Core.Abstractions;
using DiffPdf.Messaging.Retention;
using DiffPdf.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DiffPdf.Core.Tests;

public class RetentionServiceTests
{
    private sealed class FakeLeaderElection(bool isLeader) : ILeaderElection
    {
        public Task<bool> TryAcquireAsync(string role, string owner, TimeSpan lease, CancellationToken ct = default) =>
            Task.FromResult(isLeader);
    }

    private sealed class StubWorkerId(string id) : IWorkerInstanceIdProvider
    {
        public string WorkerInstanceId { get; } = id;
    }

    private sealed class ThrowingScopeFactory : IServiceScopeFactory
    {
        public IServiceScope CreateScope() =>
            throw new InvalidOperationException("A non-leader replica must not open a scope or prune.");
    }

    [Fact]
    public async Task NotLeader_Tick_DoesNoWork()
    {
        var svc = new RetentionService(
            new ThrowingScopeFactory(),
            new FakeLeaderElection(isLeader: false),
            new StubWorkerId("replica-2"),
            Options.Create(new RetentionOptions { Enabled = true }),
            NullLogger<RetentionService>.Instance);

        // No leadership → returns before resolving the job store / paths (ThrowingScopeFactory not hit).
        await svc.TickAsync(new RetentionOptions { Enabled = true }, CancellationToken.None);
    }
}
