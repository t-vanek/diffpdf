using System;

namespace DiffPdf.DesktopUI.ViewModels;

/// <summary>The friendly schedule frequencies the editor offers in place of a raw cron expression.</summary>
public enum ScheduleKind { None, Interval, Daily, Weekly, Monthly, Custom }

/// <summary>One frequency choice in the "Plán (čas)" dropdown (value + Czech label).</summary>
public sealed record ScheduleKindOption(ScheduleKind Kind, string Label);

/// <summary>One weekday choice for a weekly schedule (value + Czech label).</summary>
public sealed record WeekdayOption(DayOfWeek Day, string Label);

/// <summary>
/// Translates between a 5-field <b>UTC</b> cron (what the server schedules on, via Cronos in
/// <see cref="TimeZoneInfo.Utc"/>) and the friendly "denně / týdně / měsíčně v HH:mm <i>místního</i> času"
/// the editor shows — so users never type cron. The local↔UTC conversion uses the supplied time zone at a
/// concrete reference date, so a weekday/day-of-month that crosses midnight in UTC is handled correctly.
/// One inherent limitation of any UTC cron: across a summer/winter-time switch the fired wall-clock time can
/// shift by an hour (the conversion is pinned to the reference date's offset). The methods take the zone and
/// reference date as parameters so the behaviour is deterministic and unit-testable regardless of host TZ.
/// </summary>
public static class CronSchedule
{
    /// <summary>UTC cron firing every day at the given local time of day.</summary>
    public static string Daily(TimeSpan localTime, TimeZoneInfo tz, DateTime referenceLocalDate)
    {
        var (m, h, _, _) = ToUtcFields(referenceLocalDate.Date + localTime, tz);
        return $"{m} {h} * * *";
    }

    /// <summary>UTC cron firing weekly on the given local weekday at the given local time.</summary>
    public static string Weekly(DayOfWeek localDay, TimeSpan localTime, TimeZoneInfo tz, DateTime referenceLocalDate)
    {
        int delta = ((int)localDay - (int)referenceLocalDate.DayOfWeek + 7) % 7;
        var (m, h, utcDow, _) = ToUtcFields(referenceLocalDate.Date.AddDays(delta) + localTime, tz);
        return $"{m} {h} * * {(int)utcDow}";
    }

    /// <summary>UTC cron firing monthly on the given local day-of-month (1–28) at the given local time.</summary>
    public static string Monthly(int localDayOfMonth, TimeSpan localTime, TimeZoneInfo tz, DateTime referenceLocalDate)
    {
        int dom = Math.Clamp(localDayOfMonth, 1, 28);
        var date = new DateTime(referenceLocalDate.Year, referenceLocalDate.Month, dom);
        var (m, h, _, utcDay) = ToUtcFields(date + localTime, tz);
        return $"{m} {h} {utcDay} * *";
    }

    /// <summary>
    /// Parses a cron the editor itself could have produced (a daily/weekly/monthly single-time pattern) back
    /// into friendly fields. Returns false for anything richer — lists, ranges, steps, month restrictions, or a
    /// day-of-month that landed outside 1–28 after a UTC wrap — so the caller falls back to the raw "Vlastní" box.
    /// </summary>
    public static bool TryParse(
        string cron, TimeZoneInfo tz, DateTime referenceLocalDate,
        out ScheduleKind kind, out TimeSpan localTime, out DayOfWeek localDay, out int localDayOfMonth)
    {
        kind = ScheduleKind.Custom;
        localTime = TimeSpan.Zero;
        localDay = DayOfWeek.Monday;
        localDayOfMonth = 1;

        var f = cron.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (f.Length != 5) return false;
        if (!TryField(f[0], out int min) || !TryField(f[1], out int hour)) return false;
        if (f[3] != "*") return false; // month restrictions aren't part of the friendly model

        bool domStar = f[2] == "*", dowStar = f[4] == "*";

        // Daily: m h * * *  — the date is irrelevant, so only the (UTC→local) time of day is read back.
        if (domStar && dowStar)
        {
            kind = ScheduleKind.Daily;
            localTime = UtcTimeToLocal(referenceLocalDate, hour, min, tz).TimeOfDay;
            return true;
        }

        // Weekly: m h * * <dow>  — convert a concrete UTC instant on that weekday back to local.
        if (domStar && !dowStar && TryField(f[4], out int dow) && dow is >= 0 and <= 7)
        {
            var utc = UtcInstantOnWeekday(referenceLocalDate, NormalizeDow(dow), hour, min);
            var local = TimeZoneInfo.ConvertTimeFromUtc(utc, tz);
            kind = ScheduleKind.Weekly;
            localTime = local.TimeOfDay;
            localDay = local.DayOfWeek;
            return true;
        }

        // Monthly: m h <dom> * *  — convert a concrete UTC instant on that day back to local.
        if (!domStar && dowStar && TryField(f[2], out int day) && day is >= 1 and <= 28)
        {
            var utc = new DateTime(referenceLocalDate.Year, referenceLocalDate.Month, day, hour, min, 0, DateTimeKind.Utc);
            var local = TimeZoneInfo.ConvertTimeFromUtc(utc, tz);
            kind = ScheduleKind.Monthly;
            localTime = local.TimeOfDay;
            localDayOfMonth = local.Day;
            return true;
        }

        return false;
    }

    private static (int Minute, int Hour, DayOfWeek Dow, int Day) ToUtcFields(DateTime local, TimeZoneInfo tz)
    {
        var utc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), tz);
        return (utc.Minute, utc.Hour, utc.DayOfWeek, utc.Day);
    }

    private static DateTime UtcTimeToLocal(DateTime referenceLocalDate, int hour, int minute, TimeZoneInfo tz)
    {
        var utc = new DateTime(referenceLocalDate.Year, referenceLocalDate.Month, referenceLocalDate.Day, hour, minute, 0, DateTimeKind.Utc);
        return TimeZoneInfo.ConvertTimeFromUtc(utc, tz);
    }

    private static DateTime UtcInstantOnWeekday(DateTime referenceLocalDate, DayOfWeek dow, int hour, int minute)
    {
        var d = referenceLocalDate.Date;
        int delta = ((int)dow - (int)d.DayOfWeek + 7) % 7;
        d = d.AddDays(delta);
        return new DateTime(d.Year, d.Month, d.Day, hour, minute, 0, DateTimeKind.Utc);
    }

    private static DayOfWeek NormalizeDow(int dow) => (DayOfWeek)(dow == 7 ? 0 : dow); // cron allows 0 and 7 for Sunday

    /// <summary>A plain non-negative integer field (no <c>*</c>, ranges, lists or steps) — the only forms the friendly model round-trips.</summary>
    private static bool TryField(string field, out int value) => int.TryParse(field, out value) && value >= 0;
}
