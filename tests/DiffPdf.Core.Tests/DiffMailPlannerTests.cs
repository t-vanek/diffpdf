using DiffPdf.Core.Models;
using DiffPdf.Notifications;

namespace DiffPdf.Core.Tests;

public class DiffMailPlannerTests
{
    private static FilePairResult Pair(string path, FilePairStatus status, string? diffPdf = null) => new()
    {
        RelativePath = path,
        Status = status,
        HighlightedPdfPath = diffPdf,
        Similarity = 0.9,
        DifferingPages = 2,
    };

    private static BatchComparisonReport Report(params FilePairResult[] files) => new()
    {
        OldFolder = "/old",
        NewFolder = "/new",
        Files = files,
    };

    [Fact]
    public void No_file_list_selects_every_differing_pair_with_a_diff_pdf()
    {
        var report = Report(
            Pair("a.pdf", FilePairStatus.Identical),
            Pair("b.pdf", FilePairStatus.Differs, "/artifacts/b.diff.pdf"),
            Pair("sub/c.pdf", FilePairStatus.Differs, "/artifacts/c.diff.pdf"),
            Pair("d.pdf", FilePairStatus.Error),
            Pair("e.pdf", FilePairStatus.OnlyInNew));

        var plan = DiffMailPlanner.Plan(report, requestedFiles: null);

        Assert.Equal(["b.pdf", "sub/c.pdf"], plan.Items.Select(i => i.Pair.RelativePath));
        Assert.Equal(["b.diff.pdf", "c.diff.pdf"], plan.Items.Select(i => i.ArtifactFileName));
        Assert.Empty(plan.SkippedFiles);
        Assert.Empty(plan.UnknownFiles);
    }

    [Fact]
    public void Differing_pair_without_a_diff_pdf_is_reported_as_skipped()
    {
        var report = Report(
            Pair("ok.pdf", FilePairStatus.Differs, "/artifacts/ok.diff.pdf"),
            Pair("broken.pdf", FilePairStatus.Differs)); // highlight write failed → no artifact

        var plan = DiffMailPlanner.Plan(report, requestedFiles: null);

        Assert.Single(plan.Items);
        Assert.Equal(["broken.pdf"], plan.SkippedFiles);
    }

    [Fact]
    public void Explicit_file_list_picks_exactly_those_pairs_case_insensitively()
    {
        var report = Report(
            Pair("a.pdf", FilePairStatus.Differs, "/artifacts/a.diff.pdf"),
            Pair("b.pdf", FilePairStatus.Differs, "/artifacts/b.diff.pdf"));

        var plan = DiffMailPlanner.Plan(report, ["A.PDF"]);

        Assert.Equal(["a.pdf"], plan.Items.Select(i => i.Pair.RelativePath));
        Assert.Empty(plan.UnknownFiles);
    }

    [Fact]
    public void Explicit_file_list_reports_unknown_pairs()
    {
        var report = Report(Pair("a.pdf", FilePairStatus.Differs, "/artifacts/a.diff.pdf"));

        var plan = DiffMailPlanner.Plan(report, ["a.pdf", "neexistuje.pdf"]);

        Assert.Single(plan.Items);
        Assert.Equal(["neexistuje.pdf"], plan.UnknownFiles);
    }

    [Fact]
    public void Zip_entry_name_keeps_the_subfolder_so_same_named_files_stay_apart()
    {
        var item = new DiffMailItem(Pair("sub/report.pdf", FilePairStatus.Differs, "/artifacts/report.diff.pdf"), "report.diff.pdf");
        Assert.Equal("sub/report.diff.pdf", item.ZipEntryName);
    }

    [Theory]
    [InlineData(null, 2, 1024, false)]                // few small files → individual attachments
    [InlineData(null, 5, 1024, true)]                 // many files → auto-ZIP
    [InlineData(null, 2, 9 * 1024 * 1024, true)]      // large payload → auto-ZIP
    [InlineData(false, 50, 9 * 1024 * 1024, false)]   // explicit override wins
    [InlineData(true, 1, 10, true)]
    public void ShouldZip_follows_the_override_or_the_auto_thresholds(bool? zip, int count, long bytes, bool expected) =>
        Assert.Equal(expected, DiffMailPlanner.ShouldZip(zip, count, bytes));

    [Fact]
    public void ComposeBody_carries_scope_counts_files_note_and_link()
    {
        var differing = Pair("faktura.pdf", FilePairStatus.Differs, "/artifacts/faktura.diff.pdf");
        var job = new ComparisonJob
        {
            Id = Guid.NewGuid(),
            BranchId = Guid.NewGuid(),
            InstanceId = Guid.NewGuid(),
            Request = new BatchComparisonRequest { Scope = new JobScope("Alfa", "Lama") },
            Report = Report(differing, Pair("ok.pdf", FilePairStatus.Identical)),
        };
        var plan = new DiffMailPlan([new DiffMailItem(differing, "faktura.diff.pdf")], ["bez-diffu.pdf"], []);

        string body = DiffMailPlanner.ComposeBody(job, plan, note: "Oprav patičku.", zipped: false, jobLink: "http://x/api/v1/jobs/1");

        Assert.Contains("Alfa/Lama", body);
        Assert.Contains("1 odlišných z 2", body);
        Assert.Contains("faktura.pdf", body);
        Assert.Contains("Oprav patičku.", body);
        Assert.Contains("bez-diffu.pdf", body);
        Assert.Contains("http://x/api/v1/jobs/1", body);
    }
}
