using DiffPdf.Core.Models;
using DiffPdf.Messaging.ControlPlane;
using DiffPdf.Messaging.ScopeSync;
using DiffPdf.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DiffPdf.Core.Tests;

public class ControlCheckProvisionerTests
{
    private static ControlCheckProvisioner Build(
        IControlCheckStore checks, IBranchStore branches,
        AutoProvisionOptions? auto = null, string? scopeRoot = null) =>
        new(checks, branches,
            Options.Create(new ControlPlaneOptions { AutoProvision = auto ?? new AutoProvisionOptions() }),
            Options.Create(new ScopeSyncOptions { RootPath = scopeRoot }),
            NullLogger<ControlCheckProvisioner>.Instance);

    [Fact]
    public async Task EnsureBranchChecks_CreatesBranchScopedReadinessCheck()
    {
        var checks = new InMemoryControlCheckStore();
        var provisioner = Build(checks, new InMemoryBranchStore());

        await provisioner.EnsureBranchChecksAsync("Alfa");

        var check = await checks.GetByKeyAsync("readiness-Alfa");
        Assert.NotNull(check);
        Assert.Equal(CheckType.Readiness, check!.Type);
        Assert.Equal(CheckScopeKind.Branch, check.ScopeKind);
        Assert.Equal("Alfa", check.BranchKey);
        Assert.Equal(300, check.IntervalSeconds);
        Assert.Contains(NotificationEvent.ReadinessFailed, check.Events);
        Assert.Contains(NotificationEvent.CheckRecovered, check.Events);
    }

    [Fact]
    public async Task EnsureBranchChecks_IsIdempotent()
    {
        var checks = new InMemoryControlCheckStore();
        var provisioner = Build(checks, new InMemoryBranchStore());

        await provisioner.EnsureBranchChecksAsync("Alfa");
        await provisioner.EnsureBranchChecksAsync("Alfa");

        Assert.Single(await checks.ListAsync());
    }

    [Fact]
    public async Task EnsureBranchChecks_DoesNotOverwriteAnEditedOrDisabledCheck()
    {
        var checks = new InMemoryControlCheckStore();
        var provisioner = Build(checks, new InMemoryBranchStore());
        await provisioner.EnsureBranchChecksAsync("Alfa");
        var original = await checks.GetByKeyAsync("readiness-Alfa");
        await checks.UpdateAsync(original! with { Enabled = false, IntervalSeconds = 30 }, original!.Version);

        await provisioner.EnsureBranchChecksAsync("Alfa"); // must be a no-op

        var after = await checks.GetByKeyAsync("readiness-Alfa");
        Assert.False(after!.Enabled);
        Assert.Equal(30, after.IntervalSeconds);
        Assert.Single(await checks.ListAsync());
    }

    [Fact]
    public async Task EnsureBranchChecks_WhenDisabled_IsNoOp()
    {
        var checks = new InMemoryControlCheckStore();
        var provisioner = Build(checks, new InMemoryBranchStore(), new AutoProvisionOptions { Enabled = false });

        await provisioner.EnsureBranchChecksAsync("Alfa");

        Assert.Empty(await checks.ListAsync());
    }

    [Fact]
    public async Task EnsureBranchChecks_OverlongKey_SkipsWithoutThrowing()
    {
        var checks = new InMemoryControlCheckStore();
        var provisioner = Build(checks, new InMemoryBranchStore());
        string longBranch = new('a', 64); // "readiness-" + 64 chars exceeds the 64-char key limit

        await provisioner.EnsureBranchChecksAsync(longBranch); // must not throw

        Assert.Empty(await checks.ListAsync());
    }

    [Fact]
    public async Task ProvisionBaseline_WithoutScopeRoot_CreatesHealthAndRetentionOnly()
    {
        var checks = new InMemoryControlCheckStore();
        var provisioner = Build(checks, new InMemoryBranchStore()); // no scope root

        await provisioner.ProvisionBaselineAndExistingAsync();

        var health = await checks.GetByKeyAsync("health");
        Assert.Equal(CheckType.Health, health!.Type);
        var retention = await checks.GetByKeyAsync("retention");
        Assert.Equal(CheckType.Retention, retention!.Type);
        Assert.Equal("30", retention.Parameters["retentionDays"]);
        Assert.Equal("0 3 * * *", retention.Cron);
        Assert.Null(await checks.GetByKeyAsync("structure-sync")); // skipped: no ScopeSync root
    }

    [Fact]
    public async Task ProvisionBaseline_WithScopeRoot_AlsoCreatesStructureSync()
    {
        var checks = new InMemoryControlCheckStore();
        var provisioner = Build(checks, new InMemoryBranchStore(), scopeRoot: @"\\srv\root");

        await provisioner.ProvisionBaselineAndExistingAsync();

        var sync = await checks.GetByKeyAsync("structure-sync");
        Assert.Equal(CheckType.StructureSync, sync!.Type);
    }

    [Fact]
    public async Task ProvisionBaseline_BackfillsReadinessForEveryExistingBranch()
    {
        var checks = new InMemoryControlCheckStore();
        var branches = new InMemoryBranchStore();
        await branches.CreateAsync("Alfa", "Alfa");
        await branches.CreateAsync("Beta", "Beta");
        var provisioner = Build(checks, branches);

        await provisioner.ProvisionBaselineAndExistingAsync();

        Assert.NotNull(await checks.GetByKeyAsync("readiness-Alfa"));
        Assert.NotNull(await checks.GetByKeyAsync("readiness-Beta"));
    }

    [Fact]
    public async Task RemoveBranchChecks_DeletesTheReadinessCheck()
    {
        var checks = new InMemoryControlCheckStore();
        var provisioner = Build(checks, new InMemoryBranchStore());
        await provisioner.EnsureBranchChecksAsync("Alfa");

        await provisioner.RemoveBranchChecksAsync("Alfa");

        Assert.Null(await checks.GetByKeyAsync("readiness-Alfa"));
    }
}
