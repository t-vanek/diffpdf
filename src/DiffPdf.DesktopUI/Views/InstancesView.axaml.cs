using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using DiffPdf.DesktopUI.Infrastructure;
using DiffPdf.DesktopUI.ViewModels;

namespace DiffPdf.DesktopUI.Views;

public partial class InstancesView : UserControl
{
    public InstancesView()
    {
        InitializeComponent();
        // Right-click selects the row under the cursor before its context menu opens (Windows convention).
        RowContextMenu.Attach(InstancesGrid);
    }

    // Ctrl+F jumps to the search box so testers can filter long lists without reaching for the mouse.
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.F && e.KeyModifiers == KeyModifiers.Control)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    // Clicking into a row reloads the list (the explicit "Obnovit" button is gone). Selection still loads the
    // row's detail; the list reload reconciles in place, so selection + checkboxes survive. Ignore taps that miss
    // a row (header sort / scrollbar / empty area) so only a real row click triggers a refresh.
    private void OnRowTapped(object? sender, TappedEventArgs e)
    {
        if ((e.Source as Control)?.FindAncestorOfType<DataGridRow>(includeSelf: true) is null) return;
        _ = (DataContext as PageViewModel)?.ReloadAsync();
    }
}
