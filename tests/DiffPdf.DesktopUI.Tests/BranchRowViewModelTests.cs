using DiffPdf.Client;
using DiffPdf.DesktopUI.ViewModels;

namespace DiffPdf.DesktopUI.Tests;

/// <summary>The Fronta cell's text + accent (StatusText / IsActive) derived from the live queue snapshot.</summary>
public class BranchRowViewModelTests
{
    private static BranchRowViewModel Row() =>
        new(new Branch { Id = Guid.NewGuid(), Key = "alfa", Name = "alfa", Enabled = true });

    [Fact]
    public void StatusText_and_IsActive_reflect_the_queue_state()
    {
        var row = Row();

        // Queue state not loaded yet.
        Assert.Equal("—", row.StatusText);
        Assert.False(row.IsActive);

        // Loaded, nothing happening.
        row.Apply(new BranchQueueState { BranchKey = "alfa" });
        Assert.Equal("Nečinné", row.StatusText);
        Assert.False(row.IsActive);

        // Running + queued → active, with counts.
        row.Apply(new BranchQueueState { BranchKey = "alfa", Running = 1, Queued = 2 });
        Assert.True(row.IsActive);
        Assert.Contains("běží 1", row.StatusText);
        Assert.Contains("čeká 2", row.StatusText);

        // Queue held even with nothing running still counts as active (shows the hold).
        row.Apply(new BranchQueueState { BranchKey = "alfa", QueuePaused = true });
        Assert.True(row.IsActive);
        Assert.Contains("Fronta pozastavena", row.StatusText);
    }
}
