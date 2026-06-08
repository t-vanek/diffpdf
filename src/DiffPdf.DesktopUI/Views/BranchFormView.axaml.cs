using Avalonia.Controls;
using DiffPdf.DesktopUI.ViewModels;

namespace DiffPdf.DesktopUI.Views;

public partial class BranchFormView : UserControl
{
    public BranchFormView()
    {
        InitializeComponent();
        // Put the cursor in the first editable field so the user can type immediately (Key is read-only when editing).
        Loaded += (_, _) =>
        {
            if (DataContext is BranchFormViewModel { IsEditMode: true }) NameBox.Focus();
            else KeyBox.Focus();
        };
    }
}
