using DiffPdf.Core.Abstractions;

namespace DiffPdf.Core.Tests;

public class AutomationHeartbeatTests
{
    [Fact]
    public void Record_TracksTicks_LeaderActivity_AndErrors()
    {
        var hb = new AutomationHeartbeat();

        hb.Record("scheduler", leaderActive: true);
        hb.Record("scheduler", leaderActive: false);                 // standby tick
        hb.Record("scheduler", leaderActive: false, error: "boom");

        var scheduler = Assert.Single(hb.Snapshot(), s => s.Service == "scheduler");
        Assert.Equal(3, scheduler.TickCount);
        Assert.NotNull(scheduler.LastTickAt);
        Assert.NotNull(scheduler.LastLeaderActiveAt);                // set by the leader tick
        Assert.Equal("boom", scheduler.LastError);
        Assert.NotNull(scheduler.LastErrorAt);
    }

    [Fact]
    public void Snapshot_ReturnsEveryRecordedService()
    {
        var hb = new AutomationHeartbeat();
        hb.Record("scheduler", true);
        hb.Record("folder-watch", true);
        hb.Record("retention", false);

        var names = hb.Snapshot().Select(s => s.Service).ToList();
        Assert.Contains("scheduler", names);
        Assert.Contains("folder-watch", names);
        Assert.Contains("retention", names);
    }

    [Fact]
    public void Record_StandbyOnly_LeavesLeaderActiveNull()
    {
        var hb = new AutomationHeartbeat();
        hb.Record("retention", leaderActive: false);

        var retention = Assert.Single(hb.Snapshot());
        Assert.Equal(1, retention.TickCount);
        Assert.Null(retention.LastLeaderActiveAt);
        Assert.Null(retention.LastError);
    }
}
