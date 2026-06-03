using Cronos;
using DiffPdf.Core.Models;

namespace DiffPdf.Messaging.Scheduling;

/// <summary>
/// Pure, testable reconciliation of the live (enabled) schedule set against the in-memory
/// next-occurrence state. Keyed by schedule Id: a newly-seen schedule (or one whose cron changed)
/// is parsed and seeded to its next occurrence <em>without firing on that tick</em>; a due schedule
/// is returned and advanced from <c>now</c> (so it never double-fires); a schedule that has vanished
/// from the live set (deleted or disabled) is dropped. Not thread-safe — driven from one loop.
/// </summary>
public sealed class ScheduleReconciler
{
    private readonly record struct Entry(string Cron, CronExpression Parsed, DateTime? Next);

    private readonly Dictionary<Guid, Entry> _state = new();

    /// <summary>Number of schedules currently tracked (for diagnostics/tests).</summary>
    public int TrackedCount => _state.Count;

    /// <summary>
    /// Reconciles <paramref name="schedules"/> against the state at <paramref name="now"/> (must be UTC)
    /// and returns the schedules due to fire this tick. <paramref name="onInvalidCron"/> is invoked for
    /// any unparseable cron expression (the caller logs it); such a schedule is skipped, never fired.
    /// </summary>
    public IReadOnlyList<ComparisonSchedule> Reconcile(
        DateTime now,
        IReadOnlyList<ComparisonSchedule> schedules,
        Action<ComparisonSchedule, Exception>? onInvalidCron = null)
    {
        var due = new List<ComparisonSchedule>();
        var live = new HashSet<Guid>();

        foreach (var s in schedules)
        {
            live.Add(s.Id);

            // New schedule, or its cron expression changed: (re)parse and seed without firing.
            if (!_state.TryGetValue(s.Id, out var entry) || entry.Cron != s.Cron)
            {
                CronExpression parsed;
                try
                {
                    parsed = CronExpression.Parse(s.Cron);
                }
                catch (Exception ex)
                {
                    onInvalidCron?.Invoke(s, ex);
                    _state.Remove(s.Id);
                    continue;
                }
                _state[s.Id] = new Entry(s.Cron, parsed, parsed.GetNextOccurrence(now));
                continue;
            }

            if (entry.Next is { } dueAt && now >= dueAt)
            {
                due.Add(s);
                _state[s.Id] = entry with { Next = entry.Parsed.GetNextOccurrence(now) };
            }
        }

        // Drop schedules no longer present in the live set (deleted or disabled at runtime).
        foreach (var goneId in _state.Keys.Where(id => !live.Contains(id)).ToList())
            _state.Remove(goneId);

        return due;
    }
}
