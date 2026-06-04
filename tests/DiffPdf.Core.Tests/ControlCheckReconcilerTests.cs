using DiffPdf.Core.Models;
using DiffPdf.Messaging.ControlPlane;

namespace DiffPdf.Core.Tests;

public class ControlCheckReconcilerTests
{
    private static ControlCheck Check(Guid id, string? cron = null, int? interval = null, string key = "c") => new()
    {
        Id = id,
        Key = key,
        Name = key,
        Type = CheckType.Health,
        Cron = cron,
        IntervalSeconds = interval,
    };

    [Fact]
    public void NewIntervalCheck_IsSeeded_NotDueOnFirstTick()
    {
        var r = new ControlCheckReconciler();
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var check = Check(Guid.NewGuid(), interval: 60);

        var due = r.Reconcile(now, [check]);

        Assert.Empty(due);
        Assert.Equal(1, r.TrackedCount);
    }

    [Fact]
    public void IntervalCheck_BecomesDue_AfterInterval()
    {
        var r = new ControlCheckReconciler();
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var check = Check(Guid.NewGuid(), interval: 60);

        r.Reconcile(now, [check]);                       // seed
        var due = r.Reconcile(now.AddSeconds(61), [check]);

        Assert.Single(due);
    }

    [Fact]
    public void CronCheck_BecomesDue_AfterOccurrence()
    {
        var r = new ControlCheckReconciler();
        var now = new DateTime(2026, 1, 1, 0, 0, 30, DateTimeKind.Utc);
        var check = Check(Guid.NewGuid(), cron: "* * * * *"); // every minute

        r.Reconcile(now, [check]);                       // seed -> next occurrence 00:01:00
        var due = r.Reconcile(now.AddSeconds(35), [check]); // 00:01:05

        Assert.Single(due);
    }

    [Fact]
    public void ChangedCadence_Reseeds_WithoutFiring()
    {
        var r = new ControlCheckReconciler();
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var id = Guid.NewGuid();

        r.Reconcile(now, [Check(id, interval: 60)]);              // seed @60s
        var due = r.Reconcile(now.AddSeconds(61), [Check(id, interval: 600)]); // cadence changed -> reseed

        Assert.Empty(due);
    }

    [Fact]
    public void VanishedCheck_IsDropped()
    {
        var r = new ControlCheckReconciler();
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var check = Check(Guid.NewGuid(), interval: 60);

        r.Reconcile(now, [check]);
        Assert.Equal(1, r.TrackedCount);

        r.Reconcile(now.AddSeconds(10), []);
        Assert.Equal(0, r.TrackedCount);
    }

    [Fact]
    public void InvalidCadence_IsReported_AndSkipped()
    {
        var r = new ControlCheckReconciler();
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var bad = Check(Guid.NewGuid()); // no cron, no interval
        var problems = new List<string>();

        var due = r.Reconcile(now, [bad], (_, err) => problems.Add(err));

        Assert.Empty(due);
        Assert.Single(problems);
        Assert.Equal(0, r.TrackedCount);
    }

    [Fact]
    public void InvalidCron_IsReported_AndSkipped()
    {
        var r = new ControlCheckReconciler();
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var bad = Check(Guid.NewGuid(), cron: "not a cron");
        var problems = new List<string>();

        r.Reconcile(now, [bad], (_, err) => problems.Add(err));

        Assert.Single(problems);
        Assert.Equal(0, r.TrackedCount);
    }
}
