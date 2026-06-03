using DiffPdf.Messaging.Triggers;

namespace DiffPdf.Core.Tests;

public class WatchStateTests
{
    private static readonly TimeSpan Stability = TimeSpan.FromSeconds(30);

    private static FolderManifest M(int files, long size = 100, long ticks = 1000) => new(files, size, ticks);
    private static DateTimeOffset T(int seconds) => new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(seconds);

    [Fact]
    public void EmptyFolder_NeverLaunches()
    {
        var state = new WatchState();
        Assert.False(state.Observe(FolderManifest.Empty, T(0), Stability));
        Assert.False(state.Observe(FolderManifest.Empty, T(120), Stability));
    }

    [Fact]
    public void WaitsForStability_ThenLaunchesExactlyOnce()
    {
        var state = new WatchState();
        var drop = M(3);

        Assert.False(state.Observe(drop, T(0), Stability));   // first sighting -> start clock
        Assert.False(state.Observe(drop, T(20), Stability));  // stable 20s < 30s
        Assert.True(state.Observe(drop, T(31), Stability));   // stable 31s -> launch
        Assert.False(state.Observe(drop, T(40), Stability));  // same drop -> no relaunch
        Assert.False(state.Observe(drop, T(200), Stability)); // still same drop
    }

    [Fact]
    public void ChangingContent_RestartsStabilityClock()
    {
        var state = new WatchState();

        Assert.False(state.Observe(M(1), T(0), Stability));
        Assert.False(state.Observe(M(2), T(25), Stability));  // changed at 25 -> reset clock
        Assert.False(state.Observe(M(2), T(45), Stability));  // only 20s stable
        Assert.True(state.Observe(M(2), T(56), Stability));   // 31s stable -> launch
    }

    [Fact]
    public void NewDropAfterLaunch_LaunchesAgain()
    {
        var state = new WatchState();
        var first = M(2, ticks: 1000);

        Assert.False(state.Observe(first, T(0), Stability));
        Assert.True(state.Observe(first, T(31), Stability));  // launch first

        var redrop = first with { LatestWriteTicks = 2000 }; // same names, newer write time
        Assert.False(state.Observe(redrop, T(40), Stability)); // changed -> reset
        Assert.True(state.Observe(redrop, T(71), Stability));  // launch again
    }

    [Fact]
    public void GoingEmptyBetweenDrops_ResetsAndLaunchesNext()
    {
        var state = new WatchState();
        var drop = M(2);

        Assert.False(state.Observe(drop, T(0), Stability));
        Assert.True(state.Observe(drop, T(31), Stability));   // launch
        Assert.False(state.Observe(FolderManifest.Empty, T(60), Stability)); // cleared

        var next = M(5, ticks: 5000);
        Assert.False(state.Observe(next, T(70), Stability));
        Assert.True(state.Observe(next, T(101), Stability));  // new drop launches
    }
}
