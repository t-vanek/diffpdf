using Avalonia.Media;
using DiffPdf.Client;
using DiffPdf.DesktopUI.ViewModels;

namespace DiffPdf.DesktopUI.Tests;

/// <summary>The scope detail's stat builder: only Úlohy + Porovnávání (no checks/automations), with colour tones.</summary>
public class StatGroupTests
{
    [Fact]
    public void JobsAndComparisons_has_only_jobs_and_comparisons_with_tones()
    {
        var stats = new ScopeStatsResponse
        {
            Jobs = new JobStats { Completed = 2, Failed = 0 },
            Comparisons = new ComparisonStats { Completed = 1472, Waiting = 490, Failed = 3 },
            Checks = new CheckStats { Failed = 1 },           // present in the response…
            Automations = new AutomationStats { Failed = 1 }, // …but must NOT surface in the branch detail
        };

        var groups = ScopeStatGroups.JobsAndComparisons(stats);

        // Kontroly + Automatizace are intentionally gone.
        Assert.Equal(new[] { "Úlohy", "Porovnávání" }, groups.Select(g => g.Title).ToArray());

        var done = groups[0].Lines.Single(l => l.Label == "Dokončené");
        Assert.Equal(2, done.Count);
        Assert.Equal(StatTone.Good, done.Tone);

        // A zero count renders muted; a non-zero failure renders in its (red) tone — so the two differ.
        var jobsFailedZero = groups[0].Lines.Single(l => l.Label == "Selhané");
        var compFailed = groups[1].Lines.Single(l => l.Label == "Selhané");
        Assert.Equal(0, jobsFailedZero.Count);
        Assert.Equal(3, compFailed.Count);
        Assert.NotEqual(((SolidColorBrush)jobsFailedZero.Foreground).Color, ((SolidColorBrush)compFailed.Foreground).Color);
    }
}
