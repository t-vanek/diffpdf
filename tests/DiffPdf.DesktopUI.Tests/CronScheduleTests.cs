using DiffPdf.DesktopUI.ViewModels;

namespace DiffPdf.DesktopUI.Tests;

/// <summary>
/// The friendly schedule ↔ UTC-cron translation behind the Automatizace editor. A fixed +02:00 zone (no DST)
/// keeps the assertions deterministic regardless of the host time zone the tests run on.
/// </summary>
public class CronScheduleTests
{
    private static readonly TimeZoneInfo Plus2 =
        TimeZoneInfo.CreateCustomTimeZone("t+2", TimeSpan.FromHours(2), "t+2", "t+2");
    private static readonly DateTime Ref = new(2026, 6, 15); // arbitrary reference date

    [Fact]
    public void Daily_subtracts_the_offset_into_a_utc_cron()
    {
        // 06:00 local at +02:00 → 04:00 UTC, every day.
        Assert.Equal("0 4 * * *", CronSchedule.Daily(new TimeSpan(6, 0, 0), Plus2, Ref));
    }

    [Fact]
    public void Daily_handles_a_pre_dawn_time_that_wraps_to_the_previous_utc_day()
    {
        // 00:30 local at +02:00 → 22:30 UTC (previous day); daily only cares about the time of day.
        Assert.Equal("30 22 * * *", CronSchedule.Daily(new TimeSpan(0, 30, 0), Plus2, Ref));
    }

    [Fact]
    public void Weekly_emits_the_utc_weekday()
    {
        // Monday 06:00 local at +02:00 → Monday 04:00 UTC (no day wrap) → dow 1.
        Assert.Equal("0 4 * * 1", CronSchedule.Weekly(DayOfWeek.Monday, new TimeSpan(6, 0, 0), Plus2, Ref));
    }

    [Fact]
    public void Monthly_emits_the_utc_day_of_month()
    {
        Assert.Equal("0 4 15 * *", CronSchedule.Monthly(15, new TimeSpan(6, 0, 0), Plus2, Ref));
    }

    [Fact]
    public void Daily_round_trips_through_parse()
    {
        var cron = CronSchedule.Daily(new TimeSpan(6, 0, 0), Plus2, Ref);
        Assert.True(CronSchedule.TryParse(cron, Plus2, Ref, out var kind, out var time, out _, out _));
        Assert.Equal(ScheduleKind.Daily, kind);
        Assert.Equal(new TimeSpan(6, 0, 0), time);
    }

    [Fact]
    public void Weekly_round_trips_through_parse_even_across_a_utc_midnight_wrap()
    {
        // Monday 00:30 local → Sunday 22:30 UTC; parse must recover Monday 00:30 local, not Sunday.
        var cron = CronSchedule.Weekly(DayOfWeek.Monday, new TimeSpan(0, 30, 0), Plus2, Ref);
        Assert.True(CronSchedule.TryParse(cron, Plus2, Ref, out var kind, out var time, out var day, out _));
        Assert.Equal(ScheduleKind.Weekly, kind);
        Assert.Equal(DayOfWeek.Monday, day);
        Assert.Equal(new TimeSpan(0, 30, 0), time);
    }

    [Fact]
    public void Monthly_round_trips_through_parse()
    {
        var cron = CronSchedule.Monthly(15, new TimeSpan(6, 0, 0), Plus2, Ref);
        Assert.True(CronSchedule.TryParse(cron, Plus2, Ref, out var kind, out var time, out _, out var dom));
        Assert.Equal(ScheduleKind.Monthly, kind);
        Assert.Equal(15, dom);
        Assert.Equal(new TimeSpan(6, 0, 0), time);
    }

    [Theory]
    [InlineData("0 4 * * 0")] // Sunday as 0
    [InlineData("0 4 * * 7")] // Sunday as 7 (cron allows both)
    public void Parse_accepts_both_sunday_encodings(string cron)
    {
        Assert.True(CronSchedule.TryParse(cron, Plus2, Ref, out var kind, out _, out var day, out _));
        Assert.Equal(ScheduleKind.Weekly, kind);
        Assert.Equal(DayOfWeek.Sunday, day);
    }

    [Theory]
    [InlineData("*/15 * * * *")]   // step — every 15 minutes
    [InlineData("0 6 1,15 * *")]   // list — multiple days
    [InlineData("0 6 * 6 *")]      // month restriction (June only)
    [InlineData("0 9-17 * * *")]   // range
    [InlineData("0 6 * * *extra")] // malformed
    [InlineData("0 6 * *")]        // too few fields
    public void Parse_rejects_anything_outside_the_friendly_model(string cron)
    {
        Assert.False(CronSchedule.TryParse(cron, Plus2, Ref, out var kind, out _, out _, out _));
        Assert.Equal(ScheduleKind.Custom, kind);
    }
}
