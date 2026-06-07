using System.Collections.Generic;
using Avalonia.Media;
using DiffPdf.Client;

namespace DiffPdf.DesktopUI.ViewModels;

/// <summary>Semantic colour/icon hint for a <see cref="StatLine"/>.</summary>
public enum StatTone { Neutral, Good, Running, Waiting, Paused, Failed, Skipped, Warning }

/// <summary>
/// One labelled count within a <see cref="StatGroup"/> (e.g. "Běžící" → 3). <see cref="Tone"/> drives the
/// icon + colour; a zero count renders muted so only meaningful values stand out (failures in red).
/// </summary>
public sealed record StatLine(string Label, int Count)
{
    public StatTone Tone { get; init; } = StatTone.Neutral;

    public string Icon => Tone switch
    {
        StatTone.Good => "✓",
        StatTone.Running => "▶",
        StatTone.Waiting => "⏳",
        StatTone.Paused => "⏸",
        StatTone.Failed => "✗",
        StatTone.Skipped => "⤳",
        StatTone.Warning => "⚠",
        _ => "•",
    };

    /// <summary>Whole-line brush: a zero count is muted; otherwise the tone's colour.</summary>
    public IBrush Foreground => Count == 0 ? Palette.Faint : Tone switch
    {
        StatTone.Good => Palette.Good,
        StatTone.Running => Palette.Info,
        StatTone.Waiting => Palette.Warning,
        StatTone.Paused => Palette.Paused,
        StatTone.Failed => Palette.Bad,
        StatTone.Skipped => Palette.Skipped,
        StatTone.Warning => Palette.Warning,
        _ => Palette.Text,
    };

    public FontWeight Weight => Count > 0 ? FontWeight.SemiBold : FontWeight.Normal;
}

/// <summary>A named group of counts shown as a card in a branch/instance detail (e.g. "Úlohy").</summary>
public sealed record StatGroup(string Title, IReadOnlyList<StatLine> Lines);

/// <summary>Builds the per-scope statistic groups from a stats response.</summary>
public static class ScopeStatGroups
{
    /// <summary>
    /// Branch/instance detail: the two groups that matter for a scope — Úlohy + Porovnávání — with colour tones
    /// (Dokončené green, Běžící blue, Čekající amber, Selhané red). Checks/automations/triggers are intentionally
    /// omitted (they have their own pages); zero counts render muted in the view.
    /// </summary>
    public static IReadOnlyList<StatGroup> JobsAndComparisons(ScopeStatsResponse s) =>
    [
        new("Úlohy",
        [
            new("Dokončené", s.Jobs.Completed) { Tone = StatTone.Good },
            new("Běžící", s.Jobs.Running) { Tone = StatTone.Running },
            new("Ve frontě", s.Jobs.Queued) { Tone = StatTone.Waiting },
            new("Pozastavené", s.Jobs.Paused) { Tone = StatTone.Paused },
            new("Selhané", s.Jobs.Failed) { Tone = StatTone.Failed },
        ]),
        new("Porovnávání",
        [
            new("Dokončené", s.Comparisons.Completed) { Tone = StatTone.Good },
            new("Čekající", s.Comparisons.Waiting) { Tone = StatTone.Waiting },
            new("Probíhající", s.Comparisons.Running) { Tone = StatTone.Running },
            new("Selhané", s.Comparisons.Failed) { Tone = StatTone.Failed },
            new("Přeskočené", s.Comparisons.Skipped) { Tone = StatTone.Skipped },
        ]),
    ];
}
