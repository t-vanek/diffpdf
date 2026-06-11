using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using DiffPdf.DesktopUI.ViewModels;

namespace DiffPdf.DesktopUI.Views;

public partial class FileOperationDialogView : UserControl
{
    public FileOperationDialogView()
    {
        InitializeComponent();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        if (DataContext is not FileOperationDialogViewModel { ShowNameInput: true } vm)
            return;

        // Type-ready: focus the name box; on a file rename pre-select just the base name so typing
        // replaces it while the .pdf extension stays.
        NameBox.Focus();
        if (vm.Name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) && vm.Name.Length > 4)
        {
            NameBox.SelectionStart = 0;
            NameBox.SelectionEnd = vm.Name.Length - 4;
        }
        else
        {
            NameBox.SelectAll();
        }
    }
}
