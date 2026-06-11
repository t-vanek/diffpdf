using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using DiffPdf.DesktopUI.Services;
using DiffPdf.DesktopUI.ViewModels;

namespace DiffPdf.DesktopUI.Views;

public partial class FilePanelView : UserControl
{
    /// <summary>Pixels of pointer travel before a press on a row turns into a drag.</summary>
    private const double DragThreshold = 4;

    private Point? _dragOrigin;
    private PointerPressedEventArgs? _dragTrigger; // DoDragDropAsync requires the original press args
    private bool _dragging;

    public FilePanelView()
    {
        InitializeComponent();

        // Clicking or focusing anywhere in the panel makes it the active one (toolbar target).
        AddHandler(PointerPressedEvent, (_, _) => Vm?.MakeActive(), RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(GotFocusEvent, (_, _) => Vm?.MakeActive(), RoutingStrategies.Bubble, handledEventsToo: true);

        // Drop targets: PDFs from the OS upload here; rows dragged from the other panel copy/move here.
        AddHandler(DragDrop.DragOverEvent, OnDragOver, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(DragDrop.DropEvent, OnDrop, RoutingStrategies.Bubble, handledEventsToo: true);
        DragDrop.SetAllowDrop(this, true);

        // Drag source: a pressed row that travels past the threshold starts a panel→panel drag.
        ItemsGrid.AddHandler(PointerPressedEvent, OnGridPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        ItemsGrid.AddHandler(PointerMovedEvent, OnGridPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        ItemsGrid.AddHandler(PointerReleasedEvent, (_, _) => _dragOrigin = null, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private FilePanelViewModel? Vm => DataContext as FilePanelViewModel;

    /// <summary>The page view-model hosting both panels — the owner of upload/rename/delete/download/transfers.</summary>
    private FileManagerViewModel? Manager =>
        this.FindAncestorOfType<FileManagerView>()?.DataContext as FileManagerViewModel;

    // ---------------- drag source (panel → panel) ----------------

    private void OnGridPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Arm only on a plain left press on an actual row — Ctrl/Shift presses are selection gestures.
        bool armed = e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            && e.KeyModifiers == KeyModifiers.None
            && (e.Source as Visual)?.FindAncestorOfType<DataGridRow>() is not null;
        _dragOrigin = armed ? e.GetPosition(this) : null;
        _dragTrigger = armed ? e : null;
    }

    private void OnGridPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragOrigin is not { } origin || _dragTrigger is not { } trigger || _dragging)
            return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _dragOrigin = null;
            _dragTrigger = null;
            return;
        }
        var delta = e.GetPosition(this) - origin;
        if (Math.Abs(delta.X) < DragThreshold && Math.Abs(delta.Y) < DragThreshold)
            return;
        if (Vm is not { } vm || vm.SelectedItems.Count == 0)
            return;

        _dragOrigin = null;
        _dragTrigger = null;
        _dragging = true;
        _ = StartDragAsync(vm, trigger);
    }

    private async System.Threading.Tasks.Task StartDragAsync(FilePanelViewModel vm, PointerPressedEventArgs trigger)
    {
        try
        {
            // Same process on both ends: the rows park in PanelDragContext for the drag's duration and
            // the DataTransfer carries only a marker (nothing useful for other applications).
            PanelDragContext.Set(vm, vm.SelectedItems.ToList());
            var data = new DataTransfer();
            data.Add(DataTransferItem.CreateText("diffpdf-panel-drag"));
            await DragDrop.DoDragDropAsync(trigger, data, DragDropEffects.Copy | DragDropEffects.Move);
        }
        finally
        {
            PanelDragContext.Clear();
            _dragging = false;
        }
    }

    // ---------------- drop target ----------------

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (PanelDragContext.Current is { } drag)
        {
            e.DragEffects = drag.Source == Vm
                ? DragDropEffects.None // back onto its own panel — nothing to do
                : e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? DragDropEffects.Move : DragDropEffects.Copy;
            e.Handled = true;
            return;
        }
        e.DragEffects = e.DataTransfer is { } dt && dt.Contains(DataFormat.File)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        if (Vm is not { } vm)
            return;

        // Rows from the other panel: Shift = move, otherwise copy (same rules as F5/F6).
        if (PanelDragContext.Current is { } drag)
        {
            if (drag.Source == vm || Manager is not { } manager)
                return;
            _ = manager.HandlePanelDropAsync(drag.Source, vm, drag.Items, move: e.KeyModifiers.HasFlag(KeyModifiers.Shift));
            return;
        }

        // PDFs dragged from the OS land in this panel's current folder.
        vm.MakeActive();
        vm.RaiseFilesDropped(PdfPaths(e));
    }

    /// <summary>Local paths of all dropped <c>.pdf</c> files (other dropped items are ignored).</summary>
    private static IReadOnlyList<string> PdfPaths(DragEventArgs e)
    {
        var paths = new List<string>();
        if (e.DataTransfer is not { } dt || dt.TryGetFiles() is not { } files)
            return paths;
        foreach (var item in files)
            if (item is IStorageFile file)
            {
                var path = file.Path.LocalPath;
                if (path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) paths.Add(path);
            }
        return paths;
    }

    // ---------------- selection / navigation glue ----------------

    /// <summary>Keeps the view-model's multi-selection in sync (DataGrid.SelectedItems is not bindable).</summary>
    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (Vm is not { } vm || sender is not DataGrid grid)
            return;
        var selection = new List<FileListItemViewModel>();
        foreach (var item in grid.SelectedItems)
            if (item is FileListItemViewModel row)
                selection.Add(row);
        vm.SetSelection(selection);
    }

    private void OnRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        // Only a double-click on an actual row opens it (not on the header or empty space).
        if ((e.Source as Visual)?.FindAncestorOfType<DataGridRow>() is null)
            return;
        Vm?.OpenSelectedCommand.Execute(null);
    }

    private void OnGridKeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm is not { } vm)
            return;
        switch (e.Key)
        {
            case Key.Enter:
                vm.OpenSelectedCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Back:
                vm.NavigateUpCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Delete:
                InvokeManager(m => m.DeleteCommand.Execute(null));
                e.Handled = true;
                break;
            case Key.F2:
                InvokeManager(m => m.RenameCommand.Execute(null));
                e.Handled = true;
                break;
            case Key.Tab when e.KeyModifiers == KeyModifiers.None:
                // Total Commander convention: Tab in the list flips to the other panel.
                InvokeManager(m => m.SwitchPanelCommand.Execute(null));
                e.Handled = true;
                break;
            case Key.Escape when vm.IsSearchMode:
                vm.ExitSearchCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    /// <summary>Moves keyboard focus into this panel's grid (used by the page after a Tab switch).</summary>
    public void FocusGrid() => ItemsGrid.Focus();

    // Context-menu actions owned by the page view-model — activate this panel first so they target it.
    private void OnUploadHere(object? sender, RoutedEventArgs e) => InvokeManager(m => m.UploadFilesCommand.Execute(null));
    private void OnDownload(object? sender, RoutedEventArgs e) => InvokeManager(m => m.DownloadCommand.Execute(null));
    private void OnRename(object? sender, RoutedEventArgs e) => InvokeManager(m => m.RenameCommand.Execute(null));
    private void OnDelete(object? sender, RoutedEventArgs e) => InvokeManager(m => m.DeleteCommand.Execute(null));
    private void OnCreateFolder(object? sender, RoutedEventArgs e) => InvokeManager(m => m.CreateFolderCommand.Execute(null));

    private void InvokeManager(Action<FileManagerViewModel> action)
    {
        Vm?.MakeActive();
        if (Manager is { } manager) action(manager);
    }
}
