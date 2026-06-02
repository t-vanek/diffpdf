using DiffPdf.Core.Abstractions;
using DiffPdf.Core.Models;

namespace DiffPdf.Core.Comparison;

/// <summary>
/// Word-level textual comparison of a single aligned page pair. Inserts/deletes
/// become highlighted regions (in PDF points).
/// </summary>
public sealed class TextComparer : ITextComparer
{
    private sealed class WordTextComparer(bool normalize) : IEqualityComparer<PositionedWord>
    {
        public bool Equals(PositionedWord? a, PositionedWord? b) =>
            string.Equals(Normalize(a?.Text, normalize), Normalize(b?.Text, normalize), StringComparison.Ordinal);

        public int GetHashCode(PositionedWord w) => Normalize(w.Text, normalize).GetHashCode();
    }

    internal static string Normalize(string? s, bool normalize)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        return normalize ? s.Trim() : s;
    }

    public TextPageDiff ComparePage(PageText? oldPage, PageText? newPage, ComparisonOptions options)
    {
        var oldWords = oldPage?.Words ?? [];
        var newWords = newPage?.Words ?? [];

        if (oldWords.Count == 0 && newWords.Count == 0)
            return TextPageDiff.Identical;

        var cmp = new WordTextComparer(options.NormalizeWhitespace);
        var ops = WordDiff.Diff(oldWords, newWords, cmp);

        var regions = new List<DifferenceRegion>();
        int changes = 0;
        foreach (var op in ops)
        {
            switch (op.Kind)
            {
                case EditKind.Delete when op.Old is not null:
                    regions.Add(new DifferenceRegion(DifferenceKind.Removed, op.Old.BoundingBox, OldText: op.Old.Text));
                    changes++;
                    break;
                case EditKind.Insert when op.New is not null:
                    regions.Add(new DifferenceRegion(DifferenceKind.Added, op.New.BoundingBox, NewText: op.New.Text));
                    changes++;
                    break;
            }
        }

        int total = Math.Max(1, Math.Max(oldWords.Count, newWords.Count));
        return new TextPageDiff(Math.Min(1.0, (double)changes / total), regions);
    }

    /// <summary>Jaccard similarity over the (normalized) distinct words on each page.</summary>
    public double Similarity(PageText oldPage, PageText newPage)
    {
        var a = oldPage.Words.Select(w => Normalize(w.Text, true)).Where(s => s.Length > 0).ToHashSet();
        var b = newPage.Words.Select(w => Normalize(w.Text, true)).Where(s => s.Length > 0).ToHashSet();

        if (a.Count == 0 && b.Count == 0) return 1.0; // two empty pages are "the same"
        if (a.Count == 0 || b.Count == 0) return 0.0;

        int intersection = a.Count(b.Contains);
        int union = a.Count + b.Count - intersection;
        return union == 0 ? 1.0 : (double)intersection / union;
    }
}
