using System.Collections.ObjectModel;

namespace DiffPdf.DesktopUI.ViewModels;

/// <summary>
/// Reconciles a collection of row view-models against a freshly-fetched item list <b>by key</b>: existing rows
/// are updated in place, new ones inserted at their position, gone ones removed — so the collection's identity
/// (and therefore the user's selection / scroll) survives a realtime refresh. Replaces the three slightly
/// divergent hand-rolled reconcile loops in the Branches / Instances / Jobs view-models with one tested path.
/// </summary>
public static class ListReconciler
{
    /// <param name="onAdded">Called on each newly-created row before it is inserted (e.g. wire PropertyChanged).</param>
    /// <param name="onRemoved">Called on each row about to be removed (e.g. unwire PropertyChanged).</param>
    public static void Reconcile<TItem, TRow>(
        ObservableCollection<TRow> rows,
        IReadOnlyList<TItem> items,
        Func<TItem, string> keyOf,
        Func<TRow, string> rowKeyOf,
        Func<TItem, TRow> create,
        Action<TRow, TItem> update,
        Action<TRow>? onAdded = null,
        Action<TRow>? onRemoved = null)
    {
        var incoming = new HashSet<string>(items.Select(keyOf), StringComparer.Ordinal);

        for (int i = rows.Count - 1; i >= 0; i--)
            if (!incoming.Contains(rowKeyOf(rows[i])))
            {
                onRemoved?.Invoke(rows[i]);
                rows.RemoveAt(i);
            }

        var byKey = rows.ToDictionary(rowKeyOf, StringComparer.Ordinal);

        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (byKey.TryGetValue(keyOf(item), out var row))
            {
                update(row, item);
            }
            else
            {
                var added = create(item);
                onAdded?.Invoke(added);
                rows.Insert(Math.Min(i, rows.Count), added);
            }
        }
    }
}
