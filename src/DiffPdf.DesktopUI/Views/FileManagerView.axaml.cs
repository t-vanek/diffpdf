using Avalonia.Controls;
using DiffPdf.DesktopUI.ViewModels;

namespace DiffPdf.DesktopUI.Views;

public partial class FileManagerView : UserControl
{
    public FileManagerView()
    {
        InitializeComponent();

        // Tab (handled in the panel's grid) switches the active panel — move keyboard focus with it.
        // Deliberately NOT on every activation: clicking a panel's filter box also activates it, and
        // stealing focus back to the grid would make the box untypable.
        DataContextChanged += (_, _) =>
        {
            if (DataContext is FileManagerViewModel vm)
                vm.PanelSwitchRequested += panel =>
                    (panel == vm.LeftPanel ? LeftPanelView : RightPanelView).FocusGrid();
        };
    }
}
