using Avalonia.Controls;
using Avalonia.Input;

namespace DiffPdf.DesktopUI.Views;

public partial class InstancesView : UserControl
{
    public InstancesView() => InitializeComponent();

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
}
