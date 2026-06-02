using DiffPdf.Core.Comparison;
using DiffPdf.Core.Models;

namespace DiffPdf.Core.Tests;

public class IgnoreFilterTests
{
    [Fact]
    public void TextPattern_DropsMatchingWords()
    {
        var page = TextComparerTests.Page(1, "Total", "2024-01-01", "USD");
        var options = new ComparisonOptions { IgnoreTextPatterns = [@"\d{4}-\d{2}-\d{2}"] };

        var filtered = IgnoreFilter.FilterWords(page, options);

        Assert.DoesNotContain(filtered.Words, w => w.Text == "2024-01-01");
        Assert.Contains(filtered.Words, w => w.Text == "Total");
        Assert.Contains(filtered.Words, w => w.Text == "USD");
    }

    [Fact]
    public void Region_DropsWordsInsideArea()
    {
        // Helper places all words near the top of the page (y≈700 on a 792pt page).
        var page = TextComparerTests.Page(1, "Header", "Stamp");
        var options = new ComparisonOptions
        {
            IgnoreRegions = [new IgnoreRegion { Area = new RectangleD(0, 0, 1, 0.2) }], // top 20%
        };

        var filtered = IgnoreFilter.FilterWords(page, options);

        Assert.Empty(filtered.Words);
    }

    [Fact]
    public void IgnoredTimestamp_MakesPagesEqual()
    {
        var oldPage = TextComparerTests.Page(1, "Total", "2024-01-01");
        var newPage = TextComparerTests.Page(1, "Total", "2025-06-02");
        var options = new ComparisonOptions { IgnoreTextPatterns = [@"\d{4}-\d{2}-\d{2}"] };

        var diff = new TextComparer().ComparePage(
            IgnoreFilter.FilterWords(oldPage, options),
            IgnoreFilter.FilterWords(newPage, options),
            options);

        Assert.Equal(0, diff.Score);
        Assert.Empty(diff.Regions);
    }
}
