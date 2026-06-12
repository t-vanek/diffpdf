using DiffPdf.Persistence;
using Microsoft.Extensions.DependencyInjection;
using CoreEvent = DiffPdf.Core.Models.SystemEvent;
using CoreSeverity = DiffPdf.Core.Models.SystemEventSeverity;

namespace DiffPdf.Client.Tests;

/// <summary>
/// GET /api/v1/events through the SDK: recent mode (newest first) for the initial notification-center fill,
/// and the sinceSeq replay cursor (oldest first) that lets a client catch up after a reconnect.
/// </summary>
public class SystemEventEndpointsTests : IClassFixture<InMemoryApiFactory>
{
    private readonly InMemoryApiFactory _factory;

    public SystemEventEndpointsTests(InMemoryApiFactory factory) => _factory = factory;

    private DiffPdfClient Client() => new(_factory.CreateClient());

    private async Task<(long First, long Second)> SeedTwoAsync()
    {
        var store = _factory.Services.GetRequiredService<ISystemEventStore>();
        var first = await store.AppendAsync(new CoreEvent
        {
            Type = "job.completed",
            Severity = CoreSeverity.Info,
            BranchKey = "alfa",
            InstanceKey = "lama",
            Message = "Porovnání alfa/lama dokončeno.",
        });
        var second = await store.AppendAsync(new CoreEvent
        {
            Type = "job.failed",
            Severity = CoreSeverity.Error,
            BranchKey = "alfa",
            InstanceKey = "lama",
            Message = "Porovnání alfa/lama selhalo.",
            Detail = "boom",
        });
        return (first.Seq, second.Seq);
    }

    [Fact]
    public async Task RecentMode_ReturnsNewestFirst_WithSeqAndSeverity()
    {
        var (firstSeq, secondSeq) = await SeedTwoAsync();

        var events = await Client().ListSystemEventsAsync(limit: 500);

        var failed = events.First(e => e.Seq == secondSeq);
        var completed = events.First(e => e.Seq == firstSeq);
        Assert.True(events.ToList().IndexOf(failed) < events.ToList().IndexOf(completed)); // newest first
        Assert.Equal("job.failed", failed.Type);
        Assert.Equal(SystemEventSeverity.Error, failed.Severity);
        Assert.Equal("alfa", failed.BranchKey);
        Assert.Equal("boom", failed.Detail);
    }

    [Fact]
    public async Task SinceSeq_ReplaysOnlyNewer_OldestFirst()
    {
        var (firstSeq, secondSeq) = await SeedTwoAsync();

        var replay = await Client().ListSystemEventsAsync(sinceSeq: firstSeq, limit: 500);

        Assert.DoesNotContain(replay, e => e.Seq <= firstSeq);     // exclusive cursor
        Assert.Contains(replay, e => e.Seq == secondSeq);
        Assert.Equal(replay.OrderBy(e => e.Seq).Select(e => e.Seq), replay.Select(e => e.Seq)); // oldest first
    }

    [Fact]
    public async Task MinSeverity_FiltersTheReplay()
    {
        var (firstSeq, secondSeq) = await SeedTwoAsync();

        var errors = await Client().ListSystemEventsAsync(sinceSeq: 0, limit: 500, minSeverity: SystemEventSeverity.Error);

        Assert.Contains(errors, e => e.Seq == secondSeq);
        Assert.DoesNotContain(errors, e => e.Seq == firstSeq); // Info filtered out
    }
}
