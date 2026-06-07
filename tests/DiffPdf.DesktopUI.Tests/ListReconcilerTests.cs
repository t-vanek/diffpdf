using System.Collections.ObjectModel;
using DiffPdf.DesktopUI.ViewModels;

namespace DiffPdf.DesktopUI.Tests;

/// <summary>The shared reconcile-by-key used by all list pages: update in place, insert new, drop gone — and
/// keep existing row identity so the selection survives a refresh.</summary>
public class ListReconcilerTests
{
    private sealed class Row(string key)
    {
        public string Key { get; } = key;
        public int Value { get; set; }
    }

    private static void Reconcile(ObservableCollection<Row> rows, params (string Key, int Value)[] items) =>
        ListReconciler.Reconcile(rows, items,
            keyOf: i => i.Key, rowKeyOf: r => r.Key,
            create: i => new Row(i.Key) { Value = i.Value },
            update: (r, i) => r.Value = i.Value);

    [Fact]
    public void Updates_inserts_removes_and_preserves_identity()
    {
        var rows = new ObservableCollection<Row>();
        Reconcile(rows, ("a", 1), ("b", 2), ("c", 3));
        Assert.Equal(new[] { "a", "b", "c" }, rows.Select(r => r.Key));
        var rowA = rows[0];

        // a updated, b removed, d inserted (between a and c by position).
        Reconcile(rows, ("a", 10), ("d", 4), ("c", 3));
        Assert.Equal(new[] { "a", "d", "c" }, rows.Select(r => r.Key));
        Assert.Same(rowA, rows.First(r => r.Key == "a")); // same instance → selection/scroll survives
        Assert.Equal(10, rowA.Value);                      // updated in place, not rebuilt
        Assert.DoesNotContain(rows, r => r.Key == "b");
    }

    [Fact]
    public void Add_and_remove_callbacks_fire_for_wiring()
    {
        var rows = new ObservableCollection<Row>();
        int added = 0, removed = 0;
        void Run((string, int)[] items) => ListReconciler.Reconcile(rows, items,
            i => i.Item1, r => r.Key, i => new Row(i.Item1), (_, _) => { },
            onAdded: _ => added++, onRemoved: _ => removed++);

        Run([("a", 0), ("b", 0)]);
        Run([("b", 0), ("c", 0)]);   // a removed, c added, b kept
        Assert.Equal(3, added);       // a, b, c
        Assert.Equal(1, removed);     // a
    }
}
