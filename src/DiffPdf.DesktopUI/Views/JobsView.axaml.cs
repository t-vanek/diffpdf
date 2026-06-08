using Avalonia.Controls;
using Avalonia.Input;
using DiffPdf.DesktopUI.ViewModels;

namespace DiffPdf.DesktopUI.Views;

public partial class JobsView : UserControl
{
    public JobsView() => InitializeComponent();

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
}
