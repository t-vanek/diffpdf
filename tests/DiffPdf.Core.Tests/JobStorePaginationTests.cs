using DiffPdf.Core.Models;
using DiffPdf.Persistence;

namespace DiffPdf.Core.Tests;

public class JobStorePaginationTests
{
    private static ComparisonJob Job(string branch, string instance, DateTimeOffset created) => new()
    {
        Id = Guid.NewGuid(),
        BranchId = Guid.NewGuid(),
        InstanceId = Guid.NewGuid(),
        Request = new BatchComparisonRequest { Scope = new JobScope(branch, instance) },
        CreatedAt = created,
    };

    private static async Task<InMemoryJobStore> SeedAsync(int count, string branch = "alfa", string instance = "inst1")
    {
        var store = new InMemoryJobStore();
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        for (int i = 0; i < count; i++)
            await store.CreateAsync(Job(branch, instance, t0.AddSeconds(i)));
        return store;
    }

    [Fact]
    public async Task Limit_And_Offset_PageThroughNewestFirst_WithoutOverlap()
    {
        var store = await SeedAsync(10);

        var page1 = await store.ListAsync(new JobListQuery { Limit = 4, Offset = 0 });
        var page2 = await store.ListAsync(new JobListQuery { Limit = 4, Offset = 4 });
        var page3 = await store.ListAsync(new JobListQuery { Limit = 4, Offset = 8 });

        Assert.Equal(4, page1.Count);
        Assert.Equal(4, page2.Count);
        Assert.Equal(2, page3.Count); // 10 total → last page is partial

        Assert.True(page1[0].CreatedAt > page1[3].CreatedAt); // newest first
        var ids = page1.Concat(page2).Concat(page3).Select(j => j.Id).ToList();
        Assert.Equal(10, ids.Distinct().Count()); // pages are disjoint and cover everything
    }

    [Fact]
    public async Task CountAsync_IgnoresPagingWindow_ButHonoursFilter()
    {
        var store = await SeedAsync(7, branch: "alfa");
        await store.CreateAsync(Job("beta", "inst1", new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero)));

        Assert.Equal(8, await store.CountAsync(new JobListQuery { Limit = 2, Offset = 3 })); // window ignored
        Assert.Equal(7, await store.CountAsync(new JobListQuery { BranchKey = "alfa" }));     // filter honoured
        Assert.Equal(1, await store.CountAsync(new JobListQuery { BranchKey = "beta" }));
    }
}
