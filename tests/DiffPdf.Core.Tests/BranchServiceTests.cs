using DiffPdf.Application.Scope;
using DiffPdf.Messaging.Observability;
using DiffPdf.Messaging.Scheduling;
using DiffPdf.Messaging.ScopeSync;
using DiffPdf.Persistence;
using Microsoft.Extensions.Options;

namespace DiffPdf.Core.Tests;

/// <summary>Unit coverage for the branch management logic moved out of the /branches endpoints into BranchService.</summary>
public class BranchServiceTests
{
    private sealed record Ctx(
        BranchService Svc, InMemoryBranchStore Branches, InMemoryInstanceStore Instances,
        InMemoryAuditLogStore Audit, CapturingTriggerEvents Events);

    private static Ctx Build()
    {
        var branches = new InMemoryBranchStore();
        var instances = new InMemoryInstanceStore();
        var audit = new InMemoryAuditLogStore();
        var events = new CapturingTriggerEvents();
        var svc = new BranchService(
            branches, instances, new InMemoryJobStore(), new InMemoryFilePairTaskStore(),
            new NoopAutomationProvisioner(), new NoopScopeConfigurationProvisioner(), new NoopTriggerProvisioner(),
            events, audit, new NoopInstanceStructureService(), new NoopBranchQueueDispatcher(),
            new BranchDispatchLocks(), new DiffPdfMetrics(), Options.Create(new ScopeSyncOptions()));
        return new Ctx(svc, branches, instances, audit, events);
    }

    [Fact]
    public async Task Create_InvalidKey_ThrowsValidation()
    {
        var c = Build();
        await Assert.ThrowsAsync<ScopeValidationException>(() =>
            c.Svc.CreateAsync(new CreateBranchInput("bad key!", "Name"), autoProvision: false, actor: null));
    }

    [Fact]
    public async Task Create_EmptyName_ThrowsValidation()
    {
        var c = Build();
        await Assert.ThrowsAsync<ScopeValidationException>(() =>
            c.Svc.CreateAsync(new CreateBranchInput("Alfa", "  "), autoProvision: false, actor: null));
    }

    [Fact]
    public async Task Create_DuplicateName_ThrowsConflict()
    {
        var c = Build();
        await c.Svc.CreateAsync(new CreateBranchInput("Alfa", "Shared"), autoProvision: false, actor: null);
        await Assert.ThrowsAsync<ScopeConflictException>(() =>
            c.Svc.CreateAsync(new CreateBranchInput("Beta", "shared"), autoProvision: false, actor: null));
    }

    [Fact]
    public async Task Create_Valid_Persists_Audits_AndPublishes()
    {
        var c = Build();
        var created = await c.Svc.CreateAsync(new CreateBranchInput("Gamma", "Gamma"), autoProvision: false, actor: "tester");

        Assert.NotNull(await c.Branches.GetByKeyAsync("Gamma"));
        Assert.Contains(c.Events.Events, e => e.EventType == "branch.created" && e.BranchKey == "Gamma");
        var entries = await c.Audit.ListAsync("Branch", created.Id.ToString(), 10);
        Assert.Contains(entries, e => e.Action == "branch.created" && e.Actor == "tester");
    }

    [Fact]
    public async Task Update_UnknownBranch_ReturnsNull()
    {
        var c = Build();
        Assert.Null(await c.Svc.UpdateAsync("nope", new UpdateBranchInput("X", true, 1), actor: null));
    }

    [Fact]
    public async Task Update_DuplicateName_ThrowsConflict()
    {
        var c = Build();
        await c.Svc.CreateAsync(new CreateBranchInput("Alfa", "Alfa"), autoProvision: false, actor: null);
        await c.Svc.CreateAsync(new CreateBranchInput("Beta", "Beta"), autoProvision: false, actor: null);
        await Assert.ThrowsAsync<ScopeConflictException>(() =>
            c.Svc.UpdateAsync("Beta", new UpdateBranchInput("Alfa", true, 1), actor: null));
    }

    [Fact]
    public async Task Update_VersionConflict_ThrowsConflict()
    {
        var c = Build();
        await c.Svc.CreateAsync(new CreateBranchInput("Alfa", "Alfa"), autoProvision: false, actor: null);
        // The branch is at version 1; passing a stale version makes the store raise a concurrency conflict,
        // which the service surfaces as a ScopeConflictException (→ 409 at the endpoint).
        await Assert.ThrowsAsync<ScopeConflictException>(() =>
            c.Svc.UpdateAsync("Alfa", new UpdateBranchInput("Alfa", false, 999), actor: null));
    }

    [Fact]
    public async Task Delete_UnknownBranch_ReturnsFalse()
    {
        var c = Build();
        Assert.False(await c.Svc.DeleteAsync("nope", cascade: false, actor: null));
    }

    [Fact]
    public async Task Delete_WithInstances_ThrowsConflict()
    {
        var c = Build();
        var b = await c.Svc.CreateAsync(new CreateBranchInput("Alfa", "Alfa"), autoProvision: false, actor: null);
        await c.Instances.CreateAsync(b.Id, "Lama", "Lama", "/base", null);
        await Assert.ThrowsAsync<ScopeConflictException>(() =>
            c.Svc.DeleteAsync("Alfa", cascade: false, actor: null));
    }

    [Fact]
    public async Task Delete_EmptyBranch_RemovesAndPublishes()
    {
        var c = Build();
        await c.Svc.CreateAsync(new CreateBranchInput("Solo", "Solo"), autoProvision: false, actor: null);

        Assert.True(await c.Svc.DeleteAsync("Solo", cascade: false, actor: "tester"));
        Assert.Null(await c.Branches.GetByKeyAsync("Solo"));
        Assert.Contains(c.Events.Events, e => e.EventType == "branch.deleted" && e.BranchKey == "Solo");
    }
}
