using Avalonia.Controls;
using DiffPdf.DesktopUI.Infrastructure;

namespace DiffPdf.DesktopUI.Views;

public partial class SubscriptionsView : UserControl
{
    public SubscriptionsView()
    {
        InitializeComponent();
        // Right-click selects the row under the cursor before its context menu opens (Windows convention).
        // "Nové pravidlo" must stay reachable on an empty list, so the menu opens even with no selection.
        RowContextMenu.Attach(RulesGrid, openOnEmpty: true);
    }
}
