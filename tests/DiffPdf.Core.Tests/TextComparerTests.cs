using DiffPdf.Core.Abstractions;
using DiffPdf.Core.Comparison;
using DiffPdf.Core.Models;

namespace DiffPdf.Core.Tests;

public class TextComparerTests
{
    internal static PageText Page(int number, params string[] words) => new()
    {
        PageNumber = number,
        Width = 612,
        Height = 792,
        Words = words.Select((w, i) => new PositionedWord(w, new RectangleD(i * 10, 700, 8, 10))).ToList(),
    };

    [Fact]
    public void IdenticalPage_HasNoDifferences()
    {
        var cmp = new TextComparer();
        var diff = cmp.ComparePage(Page(1, "hello", "world"), Page(1, "hello", "world"), new ComparisonOptions());

        Assert.Equal(0, diff.Score);
        Assert.Empty(diff.Regions);
    }

    [Fact]
    public void ChangedWord_ProducesAddedAndRemovedRegions()
    {
        var cmp = new TextComparer();
        var diff = cmp.ComparePage(Page(1, "hello", "world"), Page(1, "hello", "there"), new ComparisonOptions());

        Assert.True(diff.Score > 0);
        Assert.Contains(diff.Regions, r => r.Kind == DifferenceKind.Removed && r.OldText == "world");
        Assert.Contains(diff.Regions, r => r.Kind == DifferenceKind.Added && r.NewText == "there");
    }

    [Fact]
    public void Similarity_IsOneForIdentical_ZeroForDisjoint()
    {
        var cmp = new TextComparer();
        Assert.Equal(1.0, cmp.Similarity(Page(1, "a", "b", "c"), Page(1, "a", "b", "c")));
        Assert.Equal(0.0, cmp.Similarity(Page(1, "a", "b"), Page(1, "x", "y")));
    }
}
