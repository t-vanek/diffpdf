using Cronos;
using DiffPdf.Core.Models;

namespace DiffPdf.Application.Abstractions;

/// <summary>
/// Shared schedule math for automations: computes the next due time from a 5-field UTC cron (which wins)
/// or an interval. Used by the service layer (create/update), the provisioner and the engine's seeding
/// pass so every writer of <c>Automation.NextRunAt</c> computes it identically.
/// </summary>
public static class AutomationCadence
{
    /// <summary>True when the automation has a schedule trigger at all (cron or positive interval).</summary>
    public static bool HasSchedule(string? cron, int? intervalSeconds) =>
        !string.IsNullOrWhiteSpace(cron) || intervalSeconds is > 0;

    /// <summary>
    /// Computes the next occurrence strictly after <paramref name="fromUtc"/>. Returns false with
    /// <paramref name="error"/> set when the cadence is invalid (unparseable cron, or a non-positive
    /// interval). A null <paramref name="next"/> with a true return means "no schedule" (manual/event-only
    /// automation) — or a cron with no future occurrence.
    /// </summary>
    public static bool TryComputeNext(
        string? cron,
        int? intervalSeconds,
        DateTimeOffset fromUtc,
        out DateTimeOffset? next,
        out string? error)
    {
        next = null;
        error = null;

        if (!string.IsNullOrWhiteSpace(cron))
        {
            CronExpression parsed;
            try
            {
                parsed = CronExpression.Parse(cron);
            }
            catch (CronFormatException ex)
            {
                error = $"Invalid cron '{cron}': {ex.Message}";
                return false;
            }

            next = parsed.GetNextOccurrence(fromUtc, TimeZoneInfo.Utc);
            return true;
        }

        if (intervalSeconds is { } interval)
        {
            if (interval <= 0)
            {
                error = $"Interval must be positive (got {interval}).";
                return false;
            }

            next = fromUtc + TimeSpan.FromSeconds(interval);
            return true;
        }

        return true; // no schedule — manual/event-only
    }

    /// <summary>
    /// How old an in-flight run claim must be before it is considered crashed and reclaimed: the
    /// worst-case run duration (every attempt timing out, plus the retry delays) with a one-minute
    /// cushion. Shared by the engine's schedule claim, the event-trigger handler and run-now.
    /// </summary>
    public static TimeSpan StaleClaimWindow(Automation automation)
    {
        long timeout = Math.Max(1, automation.TimeoutSeconds);
        long attempts = Math.Max(1, automation.MaxAttempts);
        long retryDelay = Math.Max(0, automation.RetryDelaySeconds);
        return TimeSpan.FromSeconds(timeout * attempts + retryDelay * (attempts - 1) + 60);
    }
}
