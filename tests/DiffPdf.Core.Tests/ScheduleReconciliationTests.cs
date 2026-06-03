using DiffPdf.Core.Models;
using DiffPdf.Messaging.Scheduling;

namespace DiffPdf.Core.Tests;

public class ScheduleReconciliationTests
{
    private static ComparisonSchedule Sched(string cron, Guid id, string key = "s") => new()
    {
        Id = id,
        BranchId = Guid.NewGuid(),
        InstanceId = Guid.NewGuid(),
        BranchKey = "Alfa",
        InstanceKey = "Lama",
        Key = key,
        Name = key,
        Cron = cron,
    };

    // Fixed UTC base 12:00:30 so the next "* * * * *" occurrence is 12:01:00.
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 30, DateTimeKind.Utc);

    [Fact]
    public void NewSchedule_NoFireOnFirstSight_ThenFiresAtNextOccurrence_NoDoubleFire()
    {
        var r = new ScheduleReconciler();
        var s = Sched("* * * * *", Guid.NewGuid());

        Assert.Empty(r.Reconcile(T0, [s]));                  // seeding tick — never fires
        Assert.Empty(r.Reconcile(T0.AddSeconds(20), [s]));   // 12:00:50 < next 12:01:00
        Assert.Single(r.Reconcile(T0.AddSeconds(35), [s]));  // 12:01:05 >= 12:01:00 — fires
        Assert.Empty(r.Reconcile(T0.AddSeconds(60), [s]));   // 12:01:30 < next 12:02:00 — no double fire
        Assert.Single(r.Reconcile(T0.AddSeconds(100), [s])); // 12:02:10 >= 12:02:00 — fires again
    }

    [Fact]
    public void CronChange_ReSeeds_DoesNotFireOnEditTick()
    {
        var r = new ScheduleReconciler();
        var id = Guid.NewGuid();

        r.Reconcile(T0, [Sched("* * * * *", id)]);                          // seed (next 12:01:00)
        var due = r.Reconcile(T0.AddSeconds(35), [Sched("0 * * * *", id)]); // 12:01:05 — but cron changed, re-seed
        Assert.Empty(due);
    }

    [Fact]
    public void VanishedSchedule_IsDropped()
    {
        var r = new ScheduleReconciler();
        r.Reconcile(T0, [Sched("* * * * *", Guid.NewGuid())]);
        Assert.Equal(1, r.TrackedCount);

        r.Reconcile(T0.AddSeconds(20), []);   // no longer in the live (enabled) set
        Assert.Equal(0, r.TrackedCount);
    }

    [Fact]
    public void InvalidCron_IsSkipped_AndReported()
    {
        var r = new ScheduleReconciler();
        var id = Guid.NewGuid();
        var reported = new List<Guid>();

        var due = r.Reconcile(T0, [Sched("not-a-cron", id)], (s, _) => reported.Add(s.Id));

        Assert.Empty(due);
        Assert.Equal(0, r.TrackedCount);
        Assert.Contains(id, reported);
    }
}
