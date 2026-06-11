using System.Collections.ObjectModel;
using Avalonia.Collections;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffPdf.Client;
using DiffPdf.DesktopUI.Services;

namespace DiffPdf.DesktopUI.ViewModels;

/// <summary>
/// One panel of the two-panel file manager: its own path, item list, selection and local filter.
/// Navigation cancels any in-flight load (fast folder-hopping never races the list); rows are
/// reconciled by path so selection and scroll survive refreshes. File operations live on
/// <see cref="FileManagerViewModel"/> — the panel only navigates, lists and tracks selection.
/// </summary>
public partial class FilePanelViewModel : ViewModelBase
{
    private readonly ServerSession _session;
    private CancellationTokenSource? _loadCts;

    /// <summary>The user clicked/focused this panel — the manager makes it the active one.</summary>
    public event Action<FilePanelViewModel>? Activated;

    /// <summary>Selection changed — the manager refreshes toolbar CanExecute states.</summary>
    public event Action? SelectionChanged;

    /// <summary>PDFs dropped from the OS onto this panel (local paths) — the manager enqueues the upload.</summary>
    public event Action<FilePanelViewModel, IReadOnlyList<string>>? FilesDropped;

    public ObservableCollection<FileListItemViewModel> Items { get; } = [];

    /// <summary>Grid view over <see cref="Items"/> applying the local name filter.</summary>
    public DataGridCollectionView ItemsView { get; }

    [ObservableProperty] private string _currentPath = "";
    [ObservableProperty] private string? _parentPath;
    [ObservableProperty] private bool _isActive;

    /// <summary>Editable path box content; Enter navigates (kept in sync with <see cref="CurrentPath"/>).</summary>
    [ObservableProperty] private string _pathInput = "";

    [ObservableProperty] private string _filterText = "";
    [ObservableProperty] private FileListItemViewModel? _selectedItem;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _isEmpty;

    /// <summary>The list shows subtree-search results instead of the folder; any navigation exits it.</summary>
    [ObservableProperty] private bool _isSearchMode;
    [ObservableProperty] private string? _searchInfo;

    /// <summary>Current multi-selection, pushed from the view (DataGrid.SelectedItems is not bindable).</summary>
    public IReadOnlyList<FileListItemViewModel> SelectedItems { get; private set; } = [];

    public FilePanelViewModel(ServerSession session)
    {
        _session = session;
        ItemsView = new DataGridCollectionView(Items) { Filter = MatchesFilter };
    }

    public bool HasLoaded { get; private set; }

    /// <summary>Single-item ops (rename/download) target the focused row; fall back to the lone selected one.</summary>
    public FileListItemViewModel? PrimaryItem => SelectedItem ?? (SelectedItems.Count == 1 ? SelectedItems[0] : null);

    public void MakeActive() => Activated?.Invoke(this);

    /// <summary>Called by the view when the DataGrid selection changes.</summary>
    public void SetSelection(IReadOnlyList<FileListItemViewModel> selection)
    {
        SelectedItems = selection;
        UpdateStatusText();
        SelectionChanged?.Invoke();
    }

    public void RaiseFilesDropped(IReadOnlyList<string> localPaths)
    {
        if (localPaths.Count > 0) FilesDropped?.Invoke(this, localPaths);
    }

    [RelayCommand]
    public Task LoadAsync(string? path) => RunAsync(async () =>
    {
        // A new navigation supersedes any in-flight one.
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        var cts = _loadCts = new CancellationTokenSource();

        try
        {
            var list = await _session.Require().ListFilesAsync(path, cts.Token);
            if (cts.IsCancellationRequested) return;

            IsSearchMode = false;
            SearchInfo = null;
            CurrentPath = list.CurrentPath;
            ParentPath = list.ParentPath;
            PathInput = list.CurrentPath;

            ListReconciler.Reconcile(
                Items, list.Items,
                keyOf: i => i.Path,
                rowKeyOf: r => r.Path,
                create: i => new FileListItemViewModel(i),
                update: (row, i) => row.Item = i);
            foreach (var row in Items) row.ShowFullPath = false;

            HasLoaded = true;
            ItemsView.Refresh();
            UpdateStatusText();
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // superseded by a newer navigation — not an error
        }
    });

    [RelayCommand]
    public Task RefreshAsync() => HasLoaded ? LoadAsync(CurrentPath) : LoadAsync("");

    [RelayCommand]
    private Task NavigateUpAsync() => ParentPath is { } parent ? LoadAsync(parent) : Task.CompletedTask;

    /// <summary>Enter / double-click: a folder opens; a search hit jumps to its folder and selects it;
    /// a plain file row is a no-op (downloads go through the toolbar).</summary>
    [RelayCommand]
    private async Task OpenSelectedAsync()
    {
        if (SelectedItem is not { } item) return;
        if (item.IsFolder)
        {
            await LoadAsync(item.Path);
        }
        else if (IsSearchMode)
        {
            string path = item.Path;
            await LoadAsync(ParentOf(path));
            SelectByPath(path);
        }
    }

    [RelayCommand]
    private Task NavigateToInputAsync() => LoadAsync(PathInput);

    /// <summary>Searches the current folder's subtree on the server; the query is the filter box text.</summary>
    [RelayCommand]
    private Task SearchSubtreeAsync() => RunAsync(async () =>
    {
        string query = FilterText.Trim();
        if (query.Length == 0 || !HasLoaded) return;

        _loadCts?.Cancel();
        _loadCts?.Dispose();
        var cts = _loadCts = new CancellationTokenSource();

        try
        {
            var result = await _session.Require().SearchFilesAsync(CurrentPath, query, recursive: true, cts.Token);
            if (cts.IsCancellationRequested) return;

            ListReconciler.Reconcile(
                Items, result.Items,
                keyOf: i => i.Path,
                rowKeyOf: r => r.Path,
                create: i => new FileListItemViewModel(i),
                update: (row, i) => row.Item = i);
            foreach (var row in Items) row.ShowFullPath = true; // results span the subtree → show where they live

            IsSearchMode = true;
            SearchInfo = $"{Format.Plural(result.Items.Count, "výsledek", "výsledky", "výsledků")} pro „{result.Query}“"
                + (result.Truncated ? " — zkráceno serverovým limitem" : "")
                + "   ·   Enter = přejít k souboru, Esc = zavřít";
            ItemsView.Refresh();
            UpdateStatusText();
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // superseded — not an error
        }
    });

    /// <summary>Esc / ✕ — back from search results to the folder listing.</summary>
    [RelayCommand]
    private Task ExitSearchAsync()
    {
        FilterText = "";
        return LoadAsync(CurrentPath);
    }

    private static string ParentOf(string path)
    {
        int lastSlash = path.LastIndexOf('/');
        return lastSlash < 0 ? "" : path[..lastSlash];
    }

    /// <summary>After a mutation, true when this panel displays <paramref name="directory"/> (and should refresh).</summary>
    public bool Shows(string directory) =>
        HasLoaded && string.Equals(CurrentPath, directory, StringComparison.OrdinalIgnoreCase);

    /// <summary>Selects the row with the given virtual path (e.g. a freshly created folder or renamed file).</summary>
    public void SelectByPath(string path) =>
        SelectedItem = Items.FirstOrDefault(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase));

    partial void OnFilterTextChanged(string value)
    {
        ItemsView.Refresh();
        UpdateStatusText();
    }

    private bool MatchesFilter(object item) =>
        string.IsNullOrWhiteSpace(FilterText)
        || item is FileListItemViewModel row && row.Name.Contains(FilterText.Trim(), StringComparison.OrdinalIgnoreCase);

    private void UpdateStatusText()
    {
        int folders = 0, files = 0;
        foreach (var row in Items)
        {
            if (!MatchesFilter(row)) continue;
            if (row.IsFolder) folders++;
            else files++;
        }
        IsEmpty = HasLoaded && folders == 0 && files == 0;

        string text = $"{Format.Plural(folders, "složka", "složky", "složek")} · {files} PDF";
        if (SelectedItems.Count > 0)
        {
            long bytes = SelectedItems.Sum(i => i.Item.SizeBytes ?? 0);
            text += $" · vybráno {SelectedItems.Count}";
            if (bytes > 0) text += $" ({Format.Bytes(bytes)})";
        }
        StatusText = text;
    }
}
