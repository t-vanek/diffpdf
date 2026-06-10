using DiffPdf.Application.Scope;
using DiffPdf.Core.Models;
using DiffPdf.Messaging.ScopeSync;
using DiffPdf.Persistence;
using Microsoft.Extensions.Options;

namespace DiffPdf.Core.Tests;

/// <summary>Unit coverage for the instance management logic moved out of the /branches/.../instances endpoints into InstanceService.</summary>
public class InstanceServiceTests
{
    private sealed record Ctx(
        InstanceService Svc, Branch Branch, InMemoryInstanceStore Instances,
        InMemoryAuditLogStore Audit, CapturingTriggerEvents Events);

    // Builds the service over a single pre-created branch "Alfa" (no ScopeSync root configured).
    private static async Task<Ctx> BuildAsync()
    {
        var branches = new InMemoryBranchStore();
        var instances = new InMemoryInstanceStore();
        var audit = new InMemoryAuditLogStore();
        var events = new CapturingTriggerEvents();
        var branch = await branches.CreateAsync("Alfa", "Alfa");
        var svc = new InstanceService(
            branches, instances, new InMemoryJobStore(), new NoopInstanceStructureService(),
            new NoopNetworkShareResolver(), new NoopNetworkDiscoveryService(),
            new NoopAutomationProvisioner(), new NoopTriggerProvisioner(), new NoopScopeConfigurationProvisioner(),
            events, audit, Options.Create(new ScopeSyncOptions()));
        return new Ctx(svc, branch, instances, audit, events);
    }

    [Fact]
    public async Task Create_UnknownBranch_ThrowsNotFound()
    {
        var c = await BuildAsync();
        await Assert.ThrowsAsync<ScopeNotFoundException>(() =>
            c.Svc.CreateAsync("nope", new CreateInstanceInput("Lama", "Lama", "/base", null), ensureStructure: false, autoProvision: false, actor: null));
    }

    [Fact]
    public async Task Create_InvalidKey_ThrowsValidation()
    {
        var c = await BuildAsync();
        await Assert.ThrowsAsync<ScopeValidationException>(() =>
            c.Svc.CreateAsync("Alfa", new CreateInstanceInput("bad key!", "Lama", "/base", null), ensureStructure: false, autoProvision: false, actor: null));
    }

    [Fact]
    public async Task Create_EmptyName_ThrowsValidation()
    {
        var c = await BuildAsync();
        await Assert.ThrowsAsync<ScopeValidationException>(() =>
            c.Svc.CreateAsync("Alfa", new CreateInstanceInput("Lama", "  ", "/base", null), ensureStructure: false, autoProvision: false, actor: null));
    }

    [Fact]
    public async Task Create_NoRootNoBasePath_ThrowsValidation()
    {
        var c = await BuildAsync();
        await Assert.ThrowsAsync<ScopeValidationException>(() =>
            c.Svc.CreateAsync("Alfa", new CreateInstanceInput("Lama", "Lama", "  ", null), ensureStructure: false, autoProvision: false, actor: null));
    }

    [Fact]
    public async Task Create_DuplicateName_ThrowsConflict()
    {
        var c = await BuildAsync();
        await c.Svc.CreateAsync("Alfa", new CreateInstanceInput("Lama", "Shared", "/base", null), ensureStructure: false, autoProvision: false, actor: null);
        await Assert.ThrowsAsync<ScopeConflictException>(() =>
            c.Svc.CreateAsync("Alfa", new CreateInstanceInput("Lama2", "shared", "/base", null), ensureStructure: false, autoProvision: false, actor: null));
    }

    [Fact]
    public async Task Create_Valid_Persists_Audits_AndPublishes()
    {
        var c = await BuildAsync();
        var result = await c.Svc.CreateAsync(
            "Alfa", new CreateInstanceInput("Lama", "Lama", "/base", null), ensureStructure: false, autoProvision: false, actor: "tester");

        Assert.Null(result.Structure); // ensureStructure was false
        Assert.NotNull(await c.Instances.GetByKeyAsync(c.Branch.Id, "Lama"));
        Assert.Contains(c.Events.Events, e => e.EventType == "instance.created" && e.InstanceKey == "Lama");
        var entries = await c.Audit.ListAsync("Instance", result.Instance.Id.ToString(), 10);
        Assert.Contains(entries, e => e.Action == "instance.created" && e.Actor == "tester");
    }

    [Fact]
    public async Task Update_UnknownInstance_ReturnsNull()
    {
        var c = await BuildAsync();
        Assert.Null(await c.Svc.UpdateAsync("Alfa", "nope", new UpdateInstanceInput("X", "/base", null, true, 1), actor: null));
    }

    [Fact]
    public async Task Update_EmptyBasePath_ThrowsValidation()
    {
        var c = await BuildAsync();
        await c.Svc.CreateAsync("Alfa", new CreateInstanceInput("Lama", "Lama", "/base", null), ensureStructure: false, autoProvision: false, actor: null);
        await Assert.ThrowsAsync<ScopeValidationException>(() =>
            c.Svc.UpdateAsync("Alfa", "Lama", new UpdateInstanceInput("Lama", "  ", null, true, 1), actor: null));
    }

    [Fact]
    public async Task Delete_UnknownInstance_ReturnsFalse()
    {
        var c = await BuildAsync();
        Assert.False(await c.Svc.DeleteAsync("Alfa", "nope", actor: null));
    }

    [Fact]
    public async Task List_UnknownBranch_ReturnsNull()
    {
        var c = await BuildAsync();
        Assert.Null(await c.Svc.ListAsync("nope"));
    }
}
