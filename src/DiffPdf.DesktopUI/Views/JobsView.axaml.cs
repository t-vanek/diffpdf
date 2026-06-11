using System;
using System.Linq;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.VisualTree;
using DiffPdf.DesktopUI.ViewModels;

namespace DiffPdf.DesktopUI.Views;

public partial class JobsView : UserControl
{
    public JobsView()
    {
        InitializeComponent();
        // Lazy "load more on scroll" for both grids; the visible "Načíst další" button is the reliable fallback.
        HookLoadMoreOnScroll(JobsGrid, () => (DataContext as JobsViewModel)?.LoadMoreJobsCommand);
        HookLoadMoreOnScroll(FilesGrid, () => (DataContext as JobsViewModel)?.LoadMoreFilesCommand);
    }

    // Best-effort: when the DataGrid's vertical scrollbar nears the end, invoke the supplied "load more" command.
    // The command itself guards re-entry, so a doubled event is harmless.
    private static void HookLoadMoreOnScroll(DataGrid grid, Func<ICommand?> command)
    {
        grid.Loaded += (_, _) =>
        {
            if (grid.GetVisualDescendants().OfType<ScrollBar>().FirstOrDefault(b => b.Orientation == Orientation.Vertical) is not { } bar
                || bar.Tag as string == "lm-hooked")
                return;
            bar.Tag = "lm-hooked"; // subscribe once, even if the view re-attaches
            bar.PropertyChanged += (_, e) =>
            {
                if (e.Property != RangeBase.ValueProperty) return;
                if (bar.Maximum > 0 && bar.Value >= bar.Maximum - Math.Max(bar.LargeChange, 1)
                    && command() is { } cmd && cmd.CanExecute(null))
                    cmd.Execute(null);
            };
        };
    }

    // Ctrl+F jumps to the file search box (when a job's file list is open).
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.F && e.KeyModifiers == KeyModifiers.Control)
        {
            FileSearchBox.Focus();
            FileSearchBox.SelectAll();
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    // Double-clicking a file row opens its detail in a separate window; the in-grid list stays compact (status + name).
    private void OnFileDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is JobsViewModel vm && sender is DataGrid { SelectedItem: FilePairLine line })
            vm.OpenFileDetailCommand.Execute(line);
    }

    // Clicking into a job row reloads the list (the explicit "Obnovit" button is gone). Reconcile keeps the
    // selection; the open detail is left to the selection handler + live progress (no flicker). Ignore taps that
    // miss a row (header / scrollbar / empty area) and taps on the row's controls (the multi-select CheckBox —
    // a Button subclass — and the delete button), so ticking rows doesn't fire a refresh per click.
    private void OnJobRowTapped(object? sender, TappedEventArgs e)
    {
        var source = e.Source as Control;
        if (source?.FindAncestorOfType<DataGridRow>(includeSelf: true) is null) return;
        if (source?.FindAncestorOfType<Button>(includeSelf: true) is not null) return;
        _ = (DataContext as PageViewModel)?.ReloadAsync();
    }
}
