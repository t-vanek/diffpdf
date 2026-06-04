using DiffPdf.Core.Models;
using DiffPdf.Core.Network;
using DiffPdf.Messaging.ControlPlane;
using DiffPdf.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiffPdf.Core.Tests;

public class ReadinessCheckExecutorTests
{
    private sealed class FakeStructure(Func<string, InstanceStructureReport> inspect) : IInstanceStructureService
    {
        public Task<InstanceStructureReport> InspectAsync(string basePath, string? credentialProfile, bool includeFiles = false, CancellationToken ct = default) =>
            Task.FromResult(inspect(basePath));
        public Task<InstanceStructureReport> EnsureAsync(string basePath, string? credentialProfile, bool includeFiles = false, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private static InstanceStructureReport Report(bool reachable, int oldPdfs, int newPdfs) => new(
        reachable, "/base",
        [
            new StructureItem("old", "/base/old", StructureItemState.Present) { PdfCount = oldPdfs },
            new StructureItem("new", "/base/new", StructureItemState.Present) { PdfCount = newPdfs },
            new StructureItem("reports", "/base/reports", StructureItemState.Present),
        ],
        reachable ? null : "unreachable");

    private static async Task<(ReadinessCheckExecutor, ControlCheck)> BuildInstanceScopeAsync(InstanceStructureReport report)
    {
        var branches = new InMemoryBranchStore();
        var instances = new InMemoryInstanceStore();
        var branch = await branches.CreateAsync("Alfa", "Alfa");
        await instances.CreateAsync(branch.Id, "Lama", "Lama", "/base", null);

        var executor = new ReadinessCheckExecutor(
            branches, instances, new FakeStructure(_ => report), NullLogger<ReadinessCheckExecutor>.Instance);

        var check = new ControlCheck
        {
            Id = Guid.NewGuid(), Key = "rd", Name = "rd", Type = CheckType.Readiness,
            ScopeKind = CheckScopeKind.Instance, BranchKey = "Alfa", InstanceKey = "Lama", IntervalSeconds = 60,
        };
        return (executor, check);
    }

    [Fact]
    public async Task Ready_Instance_ReturnsOk()
    {
        var (executor, check) = await BuildInstanceScopeAsync(Report(reachable: true, oldPdfs: 2, newPdfs: 2));

        var result = await executor.ExecuteAsync(check, CancellationToken.None);

        Assert.Equal(CheckRunOutcome.Ok, result.Outcome);
    }

    [Fact]
    public async Task EmptyInputs_ReturnsFailed()
    {
        var (executor, check) = await BuildInstanceScopeAsync(Report(reachable: true, oldPdfs: 0, newPdfs: 3));

        var result = await executor.ExecuteAsync(check, CancellationToken.None);

        Assert.Equal(CheckRunOutcome.Failed, result.Outcome);
        Assert.Contains("nothing to compare", result.Detail);
    }

    [Fact]
    public async Task Unreachable_ReturnsFailed()
    {
        var (executor, check) = await BuildInstanceScopeAsync(Report(reachable: false, oldPdfs: 0, newPdfs: 0));

        var result = await executor.ExecuteAsync(check, CancellationToken.None);

        Assert.Equal(CheckRunOutcome.Failed, result.Outcome);
        Assert.Contains("unreachable", result.Detail);
    }

    [Fact]
    public async Task UnknownInstance_ReturnsWarning()
    {
        var branches = new InMemoryBranchStore();
        var instances = new InMemoryInstanceStore();
        await branches.CreateAsync("Alfa", "Alfa"); // no instance created
        var executor = new ReadinessCheckExecutor(
            branches, instances, new FakeStructure(_ => Report(true, 1, 1)), NullLogger<ReadinessCheckExecutor>.Instance);
        var check = new ControlCheck
        {
            Id = Guid.NewGuid(), Key = "rd", Name = "rd", Type = CheckType.Readiness,
            ScopeKind = CheckScopeKind.Instance, BranchKey = "Alfa", InstanceKey = "Ghost", IntervalSeconds = 60,
        };

        var result = await executor.ExecuteAsync(check, CancellationToken.None);

        Assert.Equal(CheckRunOutcome.Warning, result.Outcome); // no instances in scope
    }
}
