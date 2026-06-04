using DiffPdf.Core.Models;
using DiffPdf.Core.Storage;
using DiffPdf.Persistence;

namespace DiffPdf.Core.Tests;

/// <summary>Unit tests for the new branch/instance Update and the per-scope file-pair count (in-memory stores).</summary>
public class ScopeStoreUpdateTests
{
    [Fact]
    public async Task BranchUpdate_PersistsFields_AndBumpsVersion()
    {
        var store = new InMemoryBranchStore();
        var b = await store.CreateAsync("alfa", "Alfa");

        var updated = await store.UpdateAsync(b with { Name = "Alfa 2", Enabled = false }, b.Version);

        Assert.Equal("Alfa 2", updated.Name);
        Assert.False(updated.Enabled);
        Assert.Equal(b.Version + 1, updated.Version);
        Assert.Equal("Alfa 2", (await store.GetByKeyAsync("alfa"))!.Name);
    }

    [Fact]
    public async Task BranchUpdate_StaleVersion_Throws()
    {
        var store = new InMemoryBranchStore();
        var b = await store.CreateAsync("alfa", "Alfa");
        await store.UpdateAsync(b with { Name = "x" }, b.Version); // -> version 2

        await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => store.UpdateAsync(b with { Name = "y" }, b.Version)); // stale (expected 1)
    }

    [Fact]
    public async Task InstanceUpdate_PersistsFields_AndBumpsVersion()
    {
        var branchId = Guid.NewGuid();
        var store = new InMemoryInstanceStore();
        var i = await store.CreateAsync(branchId, "inst1", "Inst", "/base", null);

        var updated = await store.UpdateAsync(
            i with { Name = "Inst 2", BasePath = "/base2", CredentialProfile = "corp", Enabled = false }, i.Version);

        Assert.Equal("Inst 2", updated.Name);
        Assert.Equal("/base2", updated.BasePath);
        Assert.Equal("corp", updated.CredentialProfile);
        Assert.False(updated.Enabled);
        Assert.Equal(i.Version + 1, updated.Version);
    }

    [Fact]
    public async Task FilePairTaskCount_GroupsByStatus_ForGivenJobsOnly()
    {
        var store = new InMemoryFilePairTaskStore();
        var jobA = Guid.NewGuid();
        var jobB = Guid.NewGuid();
        await store.CreateManyAsync([
            new FilePairTask { Id = Guid.NewGuid(), JobId = jobA, RelativePath = "1", Status = FilePairTaskStatus.Completed },
            new FilePairTask { Id = Guid.NewGuid(), JobId = jobA, RelativePath = "2", Status = FilePairTaskStatus.Completed },
            new FilePairTask { Id = Guid.NewGuid(), JobId = jobA, RelativePath = "3", Status = FilePairTaskStatus.Running },
            new FilePairTask { Id = Guid.NewGuid(), JobId = jobB, RelativePath = "4", Status = FilePairTaskStatus.Failed },
        ]);

        var counts = await store.CountByStatusForJobsAsync([jobA]);

        Assert.Equal(2, counts.GetValueOrDefault(FilePairTaskStatus.Completed));
        Assert.Equal(1, counts.GetValueOrDefault(FilePairTaskStatus.Running));
        Assert.False(counts.ContainsKey(FilePairTaskStatus.Failed)); // jobB is out of scope
    }

    [Fact]
    public async Task FilePairTaskCount_EmptyJobs_ReturnsEmpty()
    {
        var store = new InMemoryFilePairTaskStore();
        Assert.Empty(await store.CountByStatusForJobsAsync([]));
    }
}
