using DiffPdf.DesktopUI.ViewModels;

namespace DiffPdf.DesktopUI.Services;

/// <summary>
/// In-process payload of a panel→panel drag. Both panels live in one process, so instead of
/// serializing rows into the OS drag data, the source parks itself here for the duration of
/// <c>DoDragDropAsync</c> and the drop side reads it back; the drag's DataTransfer carries only a
/// text marker. OS-originated drags can never have this set, which is exactly the discriminator
/// the drop handler needs.
/// </summary>
public static class PanelDragContext
{
    public static (FilePanelViewModel Source, IReadOnlyList<FileListItemViewModel> Items)? Current { get; private set; }

    public static void Set(FilePanelViewModel source, IReadOnlyList<FileListItemViewModel> items) =>
        Current = (source, items);

    public static void Clear() => Current = null;
}
