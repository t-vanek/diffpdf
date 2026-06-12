using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace DiffPdf.DesktopUI.Infrastructure;

/// <summary>
/// Windows-like right-click behaviour for a DataGrid with a grid-level ContextMenu: pressing the right
/// button over a row first selects it (an existing multi-selection that already contains the row is kept,
/// so "select three, right-click one" doesn't collapse the selection), and — unless
/// <paramref name="openOnEmpty"/> — the menu stays closed while the grid has nothing selected to act on.
/// </summary>
public static class RowContextMenu
{
    public static void Attach(DataGrid grid, bool openOnEmpty = false)
    {
        // Tunnel: runs before the ContextMenu sees the gesture, so the menu always opens on the row
        // the user actually clicked — not on a stale selection.
        grid.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            if (!e.GetCurrentPoint(grid).Properties.IsRightButtonPressed) return;
            var row = (e.Source as Visual)?.FindAncestorOfType<DataGridRow>(includeSelf: true);
            if (row?.DataContext is { } item && !grid.SelectedItems.Contains(item))
                grid.SelectedItem = item;
        }, RoutingStrategies.Tunnel);

        if (!openOnEmpty && grid.ContextMenu is { } menu)
            menu.Opening += (_, e) => e.Cancel = grid.SelectedItem is null;
    }
}
