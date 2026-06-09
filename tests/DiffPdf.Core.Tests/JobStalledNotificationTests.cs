using DiffPdf.Core.Models;
using DiffPdf.Notifications;

namespace DiffPdf.Core.Tests;

public class JobStalledNotificationTests
{
    private static readonly DateTimeOffset At = new(2026, 6, 9, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Carries_JobStalled_event_and_renders_scope_and_progress()
    {
        var n = new JobStalledNotification(
            Guid.NewGuid(), "Alfa", "Lama", 3, 10, TimeSpan.FromMinutes(42), At.AddMinutes(-42), At);

        Assert.Equal(NotificationEvent.JobStalled, n.Event);
        Assert.Equal("Alfa", n.BranchKey);
        Assert.Equal("Lama", n.InstanceKey);
        Assert.Contains("Alfa/Lama", n.Title);
        Assert.Contains("3/10", n.Summary);   // progress
        Assert.Contains("42", n.Summary);      // stalled-for minutes
        Assert.Same(n, n.Detail);              // the record itself is the structured webhook payload
    }

    [Fact]
    public void Scope_is_global_when_no_branch()
    {
        var n = new JobStalledNotification(
            Guid.NewGuid(), null, null, 0, 5, TimeSpan.FromMinutes(31), default, default);
        Assert.Contains("global", n.Title);
    }
}
