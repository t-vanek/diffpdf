using DiffPdf.Core.Abstractions;
using DiffPdf.Core.Models;

namespace DiffPdf.Core.Comparison;

/// <summary>
/// Aligns pages by text similarity (Needleman–Wunsch) so an inserted or removed
/// page does not cascade into a run of false differences. Falls back to strict
/// index pairing when alignment is disabled.
/// </summary>
public sealed class PageAligner(ITextComparer textComparer) : IPageAligner
{
    public IReadOnlyList<AlignedPagePair> Align(
        IReadOnlyList<PageText> oldPages,
        IReadOnlyList<PageText> newPages,
        ComparisonOptions options)
    {
        // Fall back to cheap index pairing when alignment is disabled, or when either document is so large that the
        // O(n*m) Needleman–Wunsch matrix would risk OOM (a guard against pathological inputs).
        if (!options.AlignPages
            || (options.MaxAlignmentPages > 0
                && (oldPages.Count > options.MaxAlignmentPages || newPages.Count > options.MaxAlignmentPages)))
            return PairByIndex(oldPages.Count, newPages.Count);

        var columns = SequenceAligner.Align(
            oldPages.Count,
            newPages.Count,
            (i, j) => textComparer.Similarity(oldPages[i], newPages[j]),
            options.PageMatchThreshold);

        return columns
            .Select(c => new AlignedPagePair(
                c.OldIndex is int oi ? oldPages[oi].PageNumber : null,
                c.NewIndex is int ni ? newPages[ni].PageNumber : null))
            .ToList();
    }

    private static List<AlignedPagePair> PairByIndex(int oldCount, int newCount)
    {
        int max = Math.Max(oldCount, newCount);
        var pairs = new List<AlignedPagePair>(max);
        for (int k = 1; k <= max; k++)
            pairs.Add(new AlignedPagePair(k <= oldCount ? k : null, k <= newCount ? k : null));
        return pairs;
    }
}
