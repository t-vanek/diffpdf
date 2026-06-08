using DiffPdf.Application.ControlChecks;
using DiffPdf.Core.Models;
using DiffPdf.Messaging.ControlPlane;
using DiffPdf.Persistence;

namespace DiffPdf.Core.Tests;

/// <summary>Unit coverage for the control-check validation + CRUD logic moved out of the /checks endpoints into ControlCheckService.</summary>
public class ControlCheckServiceTests
{
    /// <summary>The runner is only reached for an existing check; these tests never get that far.</summary>
    private sealed class UnusedRunner : IControlCheckRunner
    {
        public Task<ControlCheckRun> RunAsync(ControlCheck check, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private static (ControlCheckService Svc, InMemoryControlCheckStore Store) Build()
    {
        var store = new InMemoryControlCheckStore();
        return (new ControlCheckService(store, new UnusedRunner(), new InMemoryControlCheckRunStore()), store);
    }

    private static CheckInput Input(
        string key = "chk", CheckScopeKind scope = CheckScopeKind.Global, string? branchKey = null,
        string? instanceKey = null, string? cron = "0 6 * * *", int? interval = null, string name = "Check")
        => new(key, name, CheckType.Readiness, scope, branchKey, instanceKey, cron, interval, null, null, true);

    [Fact]
    public async Task Create_InvalidKey_Throws()
    {
        var (svc, _) = Build();
        await Assert.ThrowsAsync<CheckValidationException>(() => svc.CreateAsync(Input(key: "bad key!")));
    }

    [Fact]
    public async Task Create_BranchScope_RequiresBranchKey()
    {
        var (svc, _) = Build();
        await Assert.ThrowsAsync<CheckValidationException>(() => svc.CreateAsync(Input(scope: CheckScopeKind.Branch, branchKey: null)));
    }

    [Fact]
    public async Task Create_InstanceScope_RequiresInstanceKey()
    {
        var (svc, _) = Build();
        await Assert.ThrowsAsync<CheckValidationException>(() =>
            svc.CreateAsync(Input(scope: CheckScopeKind.Instance, branchKey: "Alfa", instanceKey: null)));
    }

    [Fact]
    public async Task Create_NoSchedule_Throws()
    {
        var (svc, _) = Build();
        await Assert.ThrowsAsync<CheckValidationException>(() => svc.CreateAsync(Input(cron: null, interval: null)));
    }

    [Fact]
    public async Task Create_Valid_Persists_AndDefaultsBlankNameToKey()
    {
        var (svc, store) = Build();
        var created = await svc.CreateAsync(Input(key: "ready", name: "  ", cron: null, interval: 60));
        Assert.Equal("ready", created.Name);
        Assert.NotNull(await store.GetAsync(created.Id));
    }

    [Fact]
    public async Task Update_Unknown_ReturnsNull()
    {
        var (svc, _) = Build();
        Assert.Null(await svc.UpdateAsync(Guid.NewGuid(), Input(), version: 1));
    }

    [Fact]
    public async Task Run_Unknown_ReturnsNull()
    {
        var (svc, _) = Build();
        Assert.Null(await svc.RunAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task ListRuns_Unknown_ReturnsNull()
    {
        var (svc, _) = Build();
        Assert.Null(await svc.ListRunsAsync(Guid.NewGuid(), 50));
    }
}
