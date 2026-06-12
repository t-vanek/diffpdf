using Avalonia.Controls;

namespace DiffPdf.DesktopUI.Views;

public partial class SendDiffsDialogView : UserControl
{
    public SendDiffsDialogView()
    {
        InitializeComponent();
        // The remembered recipients are usually right — focus the field so a different address is one keystroke away.
        Loaded += (_, _) => RecipientsBox.Focus();
    }
}
