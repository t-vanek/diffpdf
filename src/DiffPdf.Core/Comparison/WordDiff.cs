namespace DiffPdf.Core.Comparison;

public enum EditKind { Equal, Insert, Delete }

public readonly record struct EditOp<T>(EditKind Kind, T? Old, T? New);

/// <summary>
/// Classic Longest-Common-Subsequence diff. Good enough for word-level PDF
/// text comparison; can be swapped for Myers later without touching callers.
/// </summary>
public static class WordDiff
{
    public static IReadOnlyList<EditOp<T>> Diff<T>(
        IReadOnlyList<T> oldItems,
        IReadOnlyList<T> newItems,
        IEqualityComparer<T>? comparer = null)
    {
        comparer ??= EqualityComparer<T>.Default;
        int n = oldItems.Count, m = newItems.Count;

        // LCS length table.
        var lcs = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
            for (int j = m - 1; j >= 0; j--)
                lcs[i, j] = comparer.Equals(oldItems[i], newItems[j])
                    ? lcs[i + 1, j + 1] + 1
                    : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

        var ops = new List<EditOp<T>>();
        int x = 0, y = 0;
        while (x < n && y < m)
        {
            if (comparer.Equals(oldItems[x], newItems[y]))
            {
                ops.Add(new EditOp<T>(EditKind.Equal, oldItems[x], newItems[y]));
                x++; y++;
            }
            else if (lcs[x + 1, y] >= lcs[x, y + 1])
            {
                ops.Add(new EditOp<T>(EditKind.Delete, oldItems[x], default));
                x++;
            }
            else
            {
                ops.Add(new EditOp<T>(EditKind.Insert, default, newItems[y]));
                y++;
            }
        }
        while (x < n) ops.Add(new EditOp<T>(EditKind.Delete, oldItems[x++], default));
        while (y < m) ops.Add(new EditOp<T>(EditKind.Insert, default, newItems[y++]));
        return ops;
    }
}
