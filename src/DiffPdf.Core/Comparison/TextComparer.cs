using DiffPdf.Core.Abstractions;
using DiffPdf.Core.Models;

namespace DiffPdf.Core.Comparison;

/// <summary>
/// Word-level textual comparison. Pages are aligned by page number; within a
/// page, words are diffed via <see cref="WordDiff"/> and every insert/delete
/// becomes a highlighted region (in PDF points).
/// </summary>
public sealed class TextComparer : ITextComparer
{
    private sealed class WordTextComparer(bool normalize) : IEqualityComparer<PositionedWord>
    {
        public bool Equals(PositionedWord? a, PositionedWord? b) =>
            string.Equals(Norm(a?.Text), Norm(b?.Text), StringComparison.Ordinal);

        public int GetHashCode(PositionedWord w) => Norm(w.Text).GetHashCode();

        private string Norm(string? s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            return normalize ? s.Trim() : s;
        }
    }

    public IReadOnlyList<PageComparison> Compare(
        IReadOnlyList<PageText> oldPages,
        IReadOnlyList<PageText> newPages,
        ComparisonOptions options)
    {
        var byNumberOld = oldPages.ToDictionary(p => p.PageNumber);
        var byNumberNew = newPages.ToDictionary(p => p.PageNumber);
        var allNumbers = byNumberOld.Keys.Union(byNumberNew.Keys).OrderBy(n => n);

        var cmp = new WordTextComparer(options.NormalizeWhitespace);
        var results = new List<PageComparison>();

        foreach (var pageNo in allNumbers)
        {
            byNumberOld.TryGetValue(pageNo, out var oldPage);
            byNumberNew.TryGetValue(pageNo, out var newPage);

            var oldWords = oldPage?.Words ?? [];
            var newWords = newPage?.Words ?? [];

            var ops = WordDiff.Diff(oldWords, newWords, cmp);

            var regions = new List<DifferenceRegion>();
            int changes = 0;
            foreach (var op in ops)
            {
                switch (op.Kind)
                {
                    case EditKind.Delete when op.Old is not null:
                        regions.Add(new DifferenceRegion(
                            DifferenceKind.Removed, op.Old.BoundingBox, OldText: op.Old.Text));
                        changes++;
                        break;
                    case EditKind.Insert when op.New is not null:
                        regions.Add(new DifferenceRegion(
                            DifferenceKind.Added, op.New.BoundingBox, NewText: op.New.Text));
                        changes++;
                        break;
                }
            }

            int total = Math.Max(1, Math.Max(oldWords.Count, newWords.Count));
            results.Add(new PageComparison
            {
                PageNumber = pageNo,
                DifferenceScore = Math.Min(1.0, (double)changes / total),
                Regions = regions,
            });
        }

        return results;
    }
}
