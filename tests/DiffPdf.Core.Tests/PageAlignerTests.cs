using DiffPdf.Core.Abstractions;
using DiffPdf.Core.Comparison;
using DiffPdf.Core.Models;

namespace DiffPdf.Core.Tests;

public class PageAlignerTests
{
    private static PageText P(int n, params string[] words) => TextComparerTests.Page(n, words);

    [Fact]
    public void InsertedPage_IsDetectedWithoutCascading()
    {
        var aligner = new PageAligner(new TextComparer());

        // new doc has an extra page (z) inserted between a and b.
        var oldPages = new[] { P(1, "a1", "a2"), P(2, "b1", "b2"), P(3, "c1", "c2") };
        var newPages = new[] { P(1, "a1", "a2"), P(2, "z1", "z2"), P(3, "b1", "b2"), P(4, "c1", "c2") };

        var pairs = aligner.Align(oldPages, newPages, new ComparisonOptions { PageMatchThreshold = 0.5 });

        var matched = pairs.Where(p => p is { OldPageNumber: not null, NewPageNumber: not null }).ToList();
        var added = pairs.Where(p => p.OldPageNumber is null).ToList();

        Assert.Equal(3, matched.Count);
        Assert.Contains(matched, p => p.OldPageNumber == 1 && p.NewPageNumber == 1);
        Assert.Contains(matched, p => p.OldPageNumber == 2 && p.NewPageNumber == 3);
        Assert.Contains(matched, p => p.OldPageNumber == 3 && p.NewPageNumber == 4);

        var insertion = Assert.Single(added);
        Assert.Equal(2, insertion.NewPageNumber);
        Assert.DoesNotContain(pairs, p => p.NewPageNumber is null);
    }

    [Fact]
    public void IndexPairing_WhenAlignmentDisabled()
    {
        var aligner = new PageAligner(new TextComparer());
        var oldPages = new[] { P(1, "a"), P(2, "b") };
        var newPages = new[] { P(1, "x"), P(2, "y"), P(3, "z") };

        var pairs = aligner.Align(oldPages, newPages, new ComparisonOptions { AlignPages = false });

        Assert.Equal(3, pairs.Count);
        Assert.Equal((1, 1), (pairs[0].OldPageNumber, pairs[0].NewPageNumber));
        Assert.Null(pairs[2].OldPageNumber);
        Assert.Equal(3, pairs[2].NewPageNumber);
    }
}
