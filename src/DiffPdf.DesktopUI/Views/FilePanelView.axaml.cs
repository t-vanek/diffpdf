using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using DiffPdf.DesktopUI.ViewModels;

namespace DiffPdf.DesktopUI.Views;

public partial class FilePanelView : UserControl
{
    public FilePanelView()
    {
        InitializeComponent();

        // Clicking or focusing anywhere in the panel makes it the active one (toolbar target).
        AddHandler(PointerPressedEvent, (_, _) => Vm?.MakeActive(), RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(GotFocusEvent, (_, _) => Vm?.MakeActive(), RoutingStrategies.Bubble, handledEventsToo: true);

        // PDFs dragged from the OS upload into this panel's current folder (same pattern as SingleCompareView).
        AddHandler(DragDrop.DragOverEvent, OnDragOver, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(DragDrop.DropEvent, OnDrop, RoutingStrategies.Bubble, handledEventsToo: true);
        DragDrop.SetAllowDrop(this, true);
    }

    private FilePanelViewModel? Vm => DataContext as FilePanelViewModel;

    /// <summary>The page view-model hosting both panels — the owner of upload/rename/delete/download.</summary>
    private FileManagerViewModel? Manager =>
        this.FindAncestorOfType<FileManagerView>()?.DataContext as FileManagerViewModel;

    private static void OnDragOver(object? sender, DragEventArgs e)
    {
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
        if ((e.Source as Avalonia.Visual)?.FindAncestorOfType<DataGridRow>() is null)
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
