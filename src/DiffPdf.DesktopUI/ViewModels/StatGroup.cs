using System.Collections.Generic;
using DiffPdf.Client;

namespace DiffPdf.DesktopUI.ViewModels;

/// <summary>One labelled count within a <see cref="StatGroup"/> (e.g. "Běžící" → 3).</summary>
public sealed record StatLine(string Label, int Count);

/// <summary>A named group of counts shown as a card in a branch/instance detail (e.g. "Úlohy").</summary>
public sealed record StatGroup(string Title, IReadOnlyList<StatLine> Lines);

/// <summary>Builds the four per-scope statistic groups (Úlohy, Kontroly, Automatizace, Porovnávání) from a stats response.</summary>
public static class ScopeStatGroups
{
    public static IReadOnlyList<StatGroup> From(ScopeStatsResponse s) =>
    [
        new("Úlohy",
        [
            new("Ve frontě", s.Jobs.Queued),
            new("Běžící", s.Jobs.Running),
            new("Pozastavené", s.Jobs.Paused),
            new("Dokončené", s.Jobs.Completed),
            new("Selhané", s.Jobs.Failed),
        ]),
        new("Kontroly",
        [
            new("Aktivní", s.Checks.Active),
            new("Probíhající", s.Checks.Running),
            new("Dokončené", s.Checks.Completed),
            new("Selhané", s.Checks.Failed),
        ]),
        new("Automatizace",
        [
            new("Aktivní", s.Automations.Active),
            new("Běžící", s.Automations.Running),
            new("Pozastavené", s.Automations.Paused),
            new("Dokončené", s.Automations.Completed),
            new("Selhané", s.Automations.Failed),
        ]),
        new("Porovnávání",
        [
            new("Čekající", s.Comparisons.Waiting),
            new("Probíhající", s.Comparisons.Running),
            new("Dokončené", s.Comparisons.Completed),
            new("Selhané", s.Comparisons.Failed),
            new("Přeskočené", s.Comparisons.Skipped),
        ]),
    ];
}
