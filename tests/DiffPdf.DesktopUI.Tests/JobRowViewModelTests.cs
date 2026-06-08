using DiffPdf.Client;
using DiffPdf.DesktopUI.ViewModels;

namespace DiffPdf.DesktopUI.Tests;

/// <summary>The Úlohy list presentation: status chip + the outcome verdict derived from the enriched JobSummary.</summary>
public class JobRowViewModelTests
{
    private static JobSummary Job(JobStatus status, int? differing = null, int? errors = null, bool? gate = null) => new()
    {
        Id = Guid.NewGuid(), BranchKey = "alfa", InstanceKey = "LamaEnergy",
        Status = status, Progress = status == JobStatus.Running ? 0.8 : 1.0,
        Differing = differing, Errors = errors, GatePassed = gate,
    };

    [Fact]
    public void Running_job_shows_status_and_no_verdict()
    {
        var r = new JobRowViewModel(Job(JobStatus.Running));
        Assert.Equal("Běží", r.StatusText);
        Assert.Equal("▶", r.StatusIcon);
        Assert.True(r.InProgress);
        Assert.False(r.HasVerdict);
    }

    [Fact]
    public void Completed_and_passed_shows_pass_verdict()
    {
        var r = new JobRowViewModel(Job(JobStatus.Completed, differing: 0, gate: true));
        Assert.True(r.HasVerdict);
        Assert.Equal("✓ Prošlo", r.VerdictText);
    }

    [Fact]
    public void Completed_with_differences_shows_count_verdict()
    {
        var r = new JobRowViewModel(Job(JobStatus.Completed, differing: 4, gate: false));
        Assert.Equal("✗ 4 odlišné", r.VerdictText); // Czech plural: 2–4 → "odlišné" (ne "odlišných")
    }

    [Fact]
    public void Apply_updates_the_row_in_place()
    {
        var r = new JobRowViewModel(Job(JobStatus.Running));
        r.Apply(Job(JobStatus.Completed, differing: 0, gate: true));
        Assert.Equal("Hotovo", r.StatusText);
        Assert.Equal("✓ Prošlo", r.VerdictText);
    }

    [Fact]
    public void FilePairLine_classifies_results_for_the_unified_list()
    {
        var differs = FilePairLine.FromResult(new FilePairResult
        {
            RelativePath = "a.pdf", Status = FilePairStatus.Differs, Similarity = 0.82, DifferingPages = 3,
            HighlightedPdfPath = @"reports\a.diff.pdf", CompareMs = 1234,
        });
        Assert.True(differs.IsDiffering);
        Assert.True(differs.HasDiff);
        Assert.Contains("82", differs.Detail);
        Assert.EndsWith("s", differs.Detail); // engine-only compare time appended (e.g. "… · 1,2 s") — culture-agnostic

        var identical = FilePairLine.FromResult(new FilePairResult { RelativePath = "b.pdf", Status = FilePairStatus.Identical, CompareMs = 300 });
        Assert.False(identical.IsDiffering);
        Assert.False(identical.HasDiff);
        Assert.Equal("300 ms", identical.Detail); // identical pair: just the compare time
    }
}
