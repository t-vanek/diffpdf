using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia.Collections;

namespace DiffPdf.DesktopUI.ViewModels;

/// <summary>A grid row that can be checkbox-selected for bulk actions.</summary>
public interface ISelectableRow : INotifyPropertyChanged
{
    bool IsSelected { get; set; }
}

/// <summary>
/// Shared "select rows for bulk actions" machinery for the list pages (Branches / Instances): the header
/// "select all" over the visible (filtered) rows, the selected count over all rows, and PropertyChanged
/// tracking that fires <paramref name="onChanged"/> whenever a row's checkbox toggles. The view-model calls
/// <see cref="Track"/> / <see cref="Untrack"/> from its reconcile add/remove callbacks. Replaces the duplicated
/// AllSelected + OnRowChanged + RecomputeSelection plumbing that lived in both view-models.
/// </summary>
public sealed class RowSelection<TRow>(DataGridCollectionView view, Func<IEnumerable<TRow>> allRows, Action onChanged)
    where TRow : class, ISelectableRow
{
    public int SelectedCount => allRows().Count(r => r.IsSelected);

    /// <summary>Header checkbox: true when every visible row is selected; setting it (un)checks all visible rows.</summary>
    public bool AllSelected
    {
        get => view.Count > 0 && view.Cast<TRow>().All(r => r.IsSelected);
        set { foreach (var r in view.Cast<TRow>()) r.IsSelected = value; }
    }

    public void Track(TRow row) => row.PropertyChanged += OnRowPropertyChanged;
    public void Untrack(TRow row) => row.PropertyChanged -= OnRowPropertyChanged;

    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ISelectableRow.IsSelected)) onChanged();
    }
}
