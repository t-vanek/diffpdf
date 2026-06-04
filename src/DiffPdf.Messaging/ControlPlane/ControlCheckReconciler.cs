using Cronos;
using DiffPdf.Core.Models;

namespace DiffPdf.Messaging.ControlPlane;

/// <summary>
/// Pure, testable reconciliation of the live (enabled) check set against the in-memory next-due state.
/// Keyed by check Id: a newly-seen check (or one whose cadence changed) is seeded to its next due time
/// <em>without firing on that tick</em>; a due check is returned and advanced (so it never double-fires);
/// a check that has vanished (deleted / disabled) is dropped. A check supports either a cron expression
/// or a fixed interval (cron wins when both are set). Not thread-safe — driven from one loop.
/// </summary>
public sealed class ControlCheckReconciler
{
    private readonly record struct Entry(string Cadence, CronExpression? Cron, TimeSpan? Interval, DateTime? Next);

    private readonly Dictionary<Guid, Entry> _state = new();

    /// <summary>Number of checks currently tracked (for diagnostics/tests).</summary>
    public int TrackedCount => _state.Count;

    /// <summary>
    /// Reconciles <paramref name="checks"/> against the state at <paramref name="now"/> (must be UTC)
    /// and returns the checks due to run this tick. <paramref name="onInvalidCadence"/> is invoked for a
    /// check with no usable cadence or an unparseable cron (the caller logs it); such a check is skipped.
    /// </summary>
    public IReadOnlyList<ControlCheck> Reconcile(
        DateTime now,
        IReadOnlyList<ControlCheck> checks,
        Action<ControlCheck, string>? onInvalidCadence = null)
    {
        var due = new List<ControlCheck>();
        var live = new HashSet<Guid>();

        foreach (var c in checks)
        {
            live.Add(c.Id);
            string cadence = $"{c.Cron}|{c.IntervalSeconds}";

            // New check, or its cadence changed: (re)parse and seed without firing.
            if (!_state.TryGetValue(c.Id, out var entry) || entry.Cadence != cadence)
            {
                if (!TrySeed(c, cadence, now, out var seeded, out string? error))
                {
                    onInvalidCadence?.Invoke(c, error!);
                    _state.Remove(c.Id);
                    continue;
                }
                _state[c.Id] = seeded;
                continue;
            }

            if (entry.Next is { } dueAt && now >= dueAt)
            {
                due.Add(c);
                _state[c.Id] = entry with { Next = NextOccurrence(entry, now) };
            }
        }

        // Drop checks no longer present in the live set (deleted or disabled at runtime).
        foreach (var goneId in _state.Keys.Where(id => !live.Contains(id)).ToList())
            _state.Remove(goneId);

        return due;
    }

    private static bool TrySeed(ControlCheck c, string cadence, DateTime now, out Entry entry, out string? error)
    {
        entry = default;
        error = null;

        if (!string.IsNullOrWhiteSpace(c.Cron))
        {
            CronExpression parsed;
            try
            {
                parsed = CronExpression.Parse(c.Cron);
            }
            catch (Exception ex)
            {
                error = $"invalid cron '{c.Cron}': {ex.Message}";
                return false;
            }
            entry = new Entry(cadence, parsed, null, parsed.GetNextOccurrence(now));
            return true;
        }

        if (c.IntervalSeconds is { } seconds && seconds > 0)
        {
            var interval = TimeSpan.FromSeconds(seconds);
            entry = new Entry(cadence, null, interval, now + interval);
            return true;
        }

        error = "no cron or positive intervalSeconds configured";
        return false;
    }

    private static DateTime? NextOccurrence(Entry entry, DateTime now) =>
        entry.Cron is not null
            ? entry.Cron.GetNextOccurrence(now)
            : now + entry.Interval!.Value;
}
