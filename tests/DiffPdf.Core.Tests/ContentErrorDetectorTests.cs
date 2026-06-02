using DiffPdf.Core.Comparison;
using DiffPdf.Core.Models;

namespace DiffPdf.Core.Tests;

public class ContentErrorDetectorTests
{
    [Fact]
    public void DetectsSubreportError()
    {
        var detector = new ContentErrorDetector();
        var pages = new[] { TextComparerTests.Page(2, "Total", "Subreport", "error", "occurred") };

        var findings = detector.Detect(pages, ContentErrorSide.New, new ComparisonOptions());

        var error = Assert.Single(findings);
        Assert.Equal(ContentErrorSide.New, error.Side);
        Assert.Equal(2, error.PageNumber);
        Assert.Contains("ubreport", error.Snippet);
    }

    [Fact]
    public void CleanPage_ProducesNoFindings()
    {
        var detector = new ContentErrorDetector();
        var pages = new[] { TextComparerTests.Page(1, "Invoice", "total", "1234") };

        Assert.Empty(detector.Detect(pages, ContentErrorSide.New, new ComparisonOptions()));
    }

    [Fact]
    public void DisabledDetection_ReturnsEmpty()
    {
        var detector = new ContentErrorDetector();
        var pages = new[] { TextComparerTests.Page(1, "subreport", "error") };

        var findings = detector.Detect(pages, ContentErrorSide.New, new ComparisonOptions { DetectContentErrors = false });
        Assert.Empty(findings);
    }
}
