using CommunityToolkit.Mvvm.ComponentModel;
using DiffPdf.Client;

namespace DiffPdf.DesktopUI.ViewModels;

/// <summary>
/// One row of a file panel: the wire item plus display helpers. Rows are reconciled in place by
/// virtual path (<see cref="ListReconciler"/>), so the DataGrid's selection and scroll survive a refresh.
/// </summary>
public partial class FileListItemViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Name), nameof(Path), nameof(IsFolder), nameof(Icon),
        nameof(SizeText), nameof(ModifiedText), nameof(TypeText), nameof(DisplayText))]
    private FileItemDto _item;

    /// <summary>Search results show their full virtual path (results span the subtree); listings show the name.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayText))]
    private bool _showFullPath;

    public FileListItemViewModel(FileItemDto item) => _item = item;

    public string Name => Item.Name;
    public string Path => Item.Path;
    public string DisplayText => ShowFullPath ? Path : Name;
    public bool IsFolder => Item.Kind == FileItemKind.Folder;
    public string Icon => IsFolder ? "📁" : "📄";
    public string SizeText => Format.Bytes(Item.SizeBytes);
    public string ModifiedText => Item.LastModified == default
        ? "—" // synthetic rows (the local drive list) have no timestamp
        : Item.LastModified.ToLocalTime().ToString("dd.MM.yyyy HH:mm");
    public string TypeText => IsFolder ? "Složka" : "PDF";
}
