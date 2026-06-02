namespace DiffPdf.Core.Comparison;

/// <summary>
/// Needleman–Wunsch global alignment over two sequences given a pairwise
/// similarity in [0,1]. Produces aligned columns where a null index on either
/// side denotes an insertion/deletion (gap).
/// </summary>
public static class SequenceAligner
{
    public static IReadOnlyList<(int? OldIndex, int? NewIndex)> Align(
        int oldCount,
        int newCount,
        Func<int, int, double> similarity,
        double matchThreshold)
    {
        // sub > 2*gap  <=>  similarity > matchThreshold, so a pair aligns on the
        // diagonal exactly when it is "similar enough", otherwise it splits into
        // a deletion + insertion.
        double gap = matchThreshold - 0.5;
        double Sub(int i, int j) => 2.0 * similarity(i, j) - 1.0;

        var score = new double[oldCount + 1, newCount + 1];
        for (int i = 1; i <= oldCount; i++) score[i, 0] = i * gap;
        for (int j = 1; j <= newCount; j++) score[0, j] = j * gap;

        for (int i = 1; i <= oldCount; i++)
        {
            for (int j = 1; j <= newCount; j++)
            {
                double diag = score[i - 1, j - 1] + Sub(i - 1, j - 1);
                double up = score[i - 1, j] + gap;
                double left = score[i, j - 1] + gap;
                score[i, j] = Math.Max(diag, Math.Max(up, left));
            }
        }

        var columns = new List<(int?, int?)>();
        int x = oldCount, y = newCount;
        while (x > 0 || y > 0)
        {
            if (x > 0 && y > 0
                && Math.Abs(score[x, y] - (score[x - 1, y - 1] + Sub(x - 1, y - 1))) < 1e-9)
            {
                columns.Add((x - 1, y - 1));
                x--; y--;
            }
            else if (x > 0 && Math.Abs(score[x, y] - (score[x - 1, y] + gap)) < 1e-9)
            {
                columns.Add((x - 1, null));
                x--;
            }
            else
            {
                columns.Add((null, y - 1));
                y--;
            }
        }

        columns.Reverse();
        return columns;
    }
}
