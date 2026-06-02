using DiffPdf.Core.Comparison;

namespace DiffPdf.Core.Tests;

public class WordDiffTests
{
    [Fact]
    public void IdenticalSequences_ProduceOnlyEquals()
    {
        var ops = WordDiff.Diff(new[] { "a", "b", "c" }, new[] { "a", "b", "c" });
        Assert.All(ops, op => Assert.Equal(EditKind.Equal, op.Kind));
    }

    [Fact]
    public void Insertion_IsDetected()
    {
        var ops = WordDiff.Diff(new[] { "a", "c" }, new[] { "a", "b", "c" });
        Assert.Contains(ops, op => op.Kind == EditKind.Insert && op.New == "b");
    }

    [Fact]
    public void Deletion_IsDetected()
    {
        var ops = WordDiff.Diff(new[] { "a", "b", "c" }, new[] { "a", "c" });
        Assert.Contains(ops, op => op.Kind == EditKind.Delete && op.Old == "b");
    }

    [Fact]
    public void CompletelyDifferent_HasNoEquals()
    {
        var ops = WordDiff.Diff(new[] { "x", "y" }, new[] { "a", "b" });
        Assert.DoesNotContain(ops, op => op.Kind == EditKind.Equal);
    }
}
