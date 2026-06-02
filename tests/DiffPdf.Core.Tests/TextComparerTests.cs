using DiffPdf.Core.Abstractions;
using DiffPdf.Core.Comparison;
using DiffPdf.Core.Models;

namespace DiffPdf.Core.Tests;

public class TextComparerTests
{
    private static PageText Page(int number, params string[] words) => new()
    {
        PageNumber = number,
        Width = 612,
        Height = 792,
        Words = words.Select((w, i) => new PositionedWord(w, new RectangleD(i * 10, 700, 8, 10))).ToList(),
    };

    [Fact]
    public void IdenticalPages_HaveNoDifferences()
    {
        var cmp = new TextComparer();
        var result = cmp.Compare(
            [Page(1, "hello", "world")],
            [Page(1, "hello", "world")],
            new ComparisonOptions());

        var page = Assert.Single(result);
        Assert.False(page.IsDifferent);
        Assert.Empty(page.Regions);
    }

    [Fact]
    public void ChangedWord_ProducesAddedAndRemovedRegions()
    {
        var cmp = new TextComparer();
        var result = cmp.Compare(
            [Page(1, "hello", "world")],
            [Page(1, "hello", "there")],
            new ComparisonOptions());

        var page = Assert.Single(result);
        Assert.True(page.IsDifferent);
        Assert.Contains(page.Regions, r => r.Kind == DifferenceKind.Removed && r.OldText == "world");
        Assert.Contains(page.Regions, r => r.Kind == DifferenceKind.Added && r.NewText == "there");
    }

    [Fact]
    public void ExtraPage_IsReportedAsDifferent()
    {
        var cmp = new TextComparer();
        var result = cmp.Compare(
            [Page(1, "a")],
            [Page(1, "a"), Page(2, "b")],
            new ComparisonOptions());

        Assert.Equal(2, result.Count);
        Assert.True(result.First(p => p.PageNumber == 2).IsDifferent);
    }
}
