using Avalonia.Controls;
using DiffPdf.DesktopUI.Infrastructure;

namespace DiffPdf.DesktopUI.Views;

public partial class AutomationDefinitionsView : UserControl
{
    public AutomationDefinitionsView()
    {
        InitializeComponent();
        // Right-click selects the row under the cursor before its context menu opens (Windows convention).
        RowContextMenu.Attach(AutomationsGrid);
    }
}
