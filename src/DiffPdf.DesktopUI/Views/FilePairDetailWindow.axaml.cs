using Avalonia.Controls;
using DiffPdf.DesktopUI.ViewModels;

namespace DiffPdf.DesktopUI.Views;

public partial class FilePairDetailWindow : Window
{
    public FilePairDetailWindow()
    {
        InitializeComponent();
        // Pull the diff artifact into the embedded webview once the window is up; remove the temp PDF on close.
        Opened += async (_, _) => { if (DataContext is FilePairDetailViewModel vm) await vm.LoadAsync(); };
        Closed += (_, _) => (DataContext as FilePairDetailViewModel)?.Cleanup();
    }
}
