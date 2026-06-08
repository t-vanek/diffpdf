using Avalonia.Controls;
using DiffPdf.DesktopUI.ViewModels;

namespace DiffPdf.DesktopUI.Views;

public partial class InstanceFormView : UserControl
{
    public InstanceFormView()
    {
        InitializeComponent();
        // Put the cursor in the first editable field so the user can type immediately (Key is read-only when editing).
        Loaded += (_, _) =>
        {
            if (DataContext is InstanceFormViewModel { IsEditMode: true }) NameBox.Focus();
            else KeyBox.Focus();
        };
    }
}
