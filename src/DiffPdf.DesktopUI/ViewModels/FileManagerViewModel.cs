using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffPdf.Client;
using DiffPdf.DesktopUI.Services;

namespace DiffPdf.DesktopUI.ViewModels;

/// <summary>
/// Soubory: two-panel PDF file manager (simple Total Commander) over the server's FileManager root.
/// Pure file management — uploading, browsing, organizing; comparisons are launched elsewhere.
/// The toolbar always targets the <see cref="ActivePanel"/>; panels own navigation and selection,
/// the upload queue owns transfer progress, and every mutation refreshes the panels that show the
/// affected folder.
/// </summary>
public partial class FileManagerViewModel : PageViewModel
{
    private readonly ServerSession _session;
    private readonly DialogService _dialogs;
    private bool _initialised;

    public override string Title => "Soubory";
    public override string Icon => "🗂";
    public override int NavOrder => 5;

    public FilePanelViewModel LeftPanel { get; }
    public FilePanelViewModel RightPanel { get; }
    public UploadQueueViewModel UploadQueue { get; }

    /// <summary>Raised only by the explicit Tab switch (not by click-activation) — the view moves keyboard focus.</summary>
    public event Action<FilePanelViewModel>? PanelSwitchRequested;

    [ObservableProperty] private FilePanelViewModel _activePanel;

    /// <summary>The transfer target: the panel that is not active.</summary>
    public FilePanelViewModel OppositePanel => ActivePanel == LeftPanel ? RightPanel : LeftPanel;

    public FileManagerViewModel(ServerSession session, DialogService dialogs)
    {
        _session = session;
        _dialogs = dialogs;

        LeftPanel = new FilePanelViewModel(session);
        RightPanel = new FilePanelViewModel(session);
        _activePanel = LeftPanel;
        LeftPanel.IsActive = true;

        foreach (var panel in new[] { LeftPanel, RightPanel })
        {
            panel.Activated += p => ActivePanel = p;
            panel.SelectionChanged += RefreshCommandStates;
            panel.FilesDropped += (p, paths) => EnqueueUpload(p, paths);
        }

        UploadQueue = new UploadQueueViewModel(session)
        {
            // The conflict question must not stall the queue invisibly — it pops the modal overwrite dialog
            // ("apply to all" offered whenever more files still wait, so one click settles the batch).
            OverwriteResolver = item => AskOverwriteAsync(
                item.FileName, showApplyToAll: UploadQueue!.Items.Any(i => i != item && i.State == UploadState.Waiting)),
        };
        UploadQueue.BatchCompleted += directories =>
        {
            foreach (string dir in directories) _ = RefreshPanelsShowingAsync(dir);
        };
    }

    public override async Task ActivateAsync()
    {
        if (_initialised) return;
        _initialised = true;
        await Task.WhenAll(LeftPanel.LoadAsync(""), RightPanel.LoadAsync(""));
    }

    public override async Task ReloadAsync()
    {
        if (!_initialised) { await ActivateAsync(); return; }
        await Task.WhenAll(LeftPanel.RefreshAsync(), RightPanel.RefreshAsync());
    }

    /// <summary>Toolbar copy/move labels point at the target — the opposite panel.</summary>
    public string CopyButtonText => ActivePanel == LeftPanel ? "Kopírovat ▸" : "◂ Kopírovat";
    public string MoveButtonText => ActivePanel == LeftPanel ? "Přesunout ▸" : "◂ Přesunout";

    partial void OnActivePanelChanged(FilePanelViewModel value)
    {
        LeftPanel.IsActive = value == LeftPanel;
        RightPanel.IsActive = value == RightPanel;
        OnPropertyChanged(nameof(CopyButtonText));
        OnPropertyChanged(nameof(MoveButtonText));
        RefreshCommandStates();
    }

    [RelayCommand]
    private void SwitchPanel()
    {
        ActivePanel = ActivePanel == LeftPanel ? RightPanel : LeftPanel;
        PanelSwitchRequested?.Invoke(ActivePanel);
    }

    // ---------------- toolbar actions (target the active panel) ----------------

    private bool HasSelection => ActivePanel.SelectedItems.Count > 0;
    private bool HasSingleSelection => ActivePanel.PrimaryItem is not null;
    private bool HasFileSelection => ActivePanel.SelectedItems.Any(i => !i.IsFolder);
    private bool CanTransfer => HasSelection && OppositePanel.HasLoaded;

    private void RefreshCommandStates()
    {
        RenameCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        DownloadCommand.NotifyCanExecuteChanged();
        CopyToOtherPanelCommand.NotifyCanExecuteChanged();
        MoveToOtherPanelCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private async Task UploadFilesAsync()
    {
        var paths = await _dialogs.OpenPdfsAsync("Vyber PDF k nahrání");
        if (paths.Count > 0)
            EnqueueUpload(ActivePanel, paths);
    }

    private void EnqueueUpload(FilePanelViewModel target, IReadOnlyList<string> localPaths)
    {
        if (!target.HasLoaded)
        {
            _dialogs.ShowToast("Panel ještě nemá načtenou složku.", ToastKind.Info);
            return;
        }
        UploadQueue.Enqueue(target.CurrentPath, localPaths);
    }

    [RelayCommand]
    private Task CreateFolderAsync() => RunAsync(async () =>
    {
        var dialog = FileOperationDialogViewModel.ForCreateFolder();
        await _dialogs.ShowFileOperationAsync(dialog);
        if (!dialog.Confirmed) return;

        var created = await _session.Require().CreateFolderAsync(
            new CreateFolderRequest(ActivePanel.CurrentPath, dialog.ResultName));
        _dialogs.ShowToast($"Složka „{created.Name}“ vytvořena.", ToastKind.Success);

        await RefreshPanelsShowingAsync(ActivePanel.CurrentPath);
        ActivePanel.SelectByPath(created.Path);
    });

    [RelayCommand(CanExecute = nameof(HasSingleSelection))]
    private Task RenameAsync() => RunAsync(async () =>
    {
        if (ActivePanel.PrimaryItem is not { } item) return;

        var dialog = FileOperationDialogViewModel.ForRename(item.Name, isFile: !item.IsFolder);
        await _dialogs.ShowFileOperationAsync(dialog);
        if (!dialog.Confirmed || dialog.ResultName == item.Name) return;

        var renamed = await _session.Require().RenameFileAsync(new RenameFileRequest(item.Path, dialog.ResultName));
        await RefreshPanelsShowingAsync(ActivePanel.CurrentPath);
        ActivePanel.SelectByPath(renamed.Path);
    });

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task DeleteAsync() => RunAsync(async () =>
    {
        var selection = ActivePanel.SelectedItems.ToList();
        if (selection.Count == 0) return;

        string what = selection.Count == 1
            ? $"„{selection[0].Name}“"
            : Format.Plural(selection.Count, "vybranou položku", "vybrané položky", "vybraných položek");
        if (!await _dialogs.ConfirmAsync("Smazat", $"Opravdu smazat {what}?"))
            return;

        var client = _session.Require();
        var nonEmptyFolders = new List<FileListItemViewModel>();
        int deleted = 0, failed = 0;

        foreach (var item in selection)
        {
            try
            {
                await client.DeleteFileAsync(item.Path);
                deleted++;
            }
            catch (DiffPdfApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Conflict && item.IsFolder)
            {
                nonEmptyFolders.Add(item); // ask once for all of them below
            }
            catch (DiffPdfApiException ex)
            {
                failed++;
                _dialogs.ShowToast($"{item.Name}: {ex.Detail ?? ex.Message}", ToastKind.Error);
            }
        }

        // Non-empty folders need an explicit, scarier confirmation — they may hold content the
        // PDF-only listing does not show.
        if (nonEmptyFolders.Count > 0)
        {
            string folders = string.Join(", ", nonEmptyFolders.Select(f => $"„{f.Name}“"));
            if (await _dialogs.ConfirmAsync(
                    "Složka není prázdná",
                    $"{folders}: složka obsahuje další položky (i takové, které tento seznam nezobrazuje). Smazat včetně celého obsahu?"))
            {
                foreach (var folder in nonEmptyFolders)
                {
                    try
                    {
                        await client.DeleteFileAsync(folder.Path, recursive: true);
                        deleted++;
                    }
                    catch (DiffPdfApiException ex)
                    {
                        failed++;
                        _dialogs.ShowToast($"{folder.Name}: {ex.Detail ?? ex.Message}", ToastKind.Error);
                    }
                }
            }
        }

        if (deleted > 0)
            _dialogs.ShowToast(failed == 0
                ? $"Smazáno: {deleted}."
                : $"Smazáno: {deleted}, chyb: {failed}.", failed == 0 ? ToastKind.Success : ToastKind.Error);

        await RefreshPanelsShowingAsync(ActivePanel.CurrentPath);
    });

    /// <summary>One selected file → save dialog; more → folder picker and a sequential download of each.</summary>
    [RelayCommand(CanExecute = nameof(HasFileSelection))]
    private Task DownloadAsync() => RunAsync(async () =>
    {
        var files = ActivePanel.SelectedItems.Where(i => !i.IsFolder).ToList();
        if (files.Count == 0) return;
        var client = _session.Require();

        if (files.Count == 1)
        {
            string? saved = await _dialogs.SaveStreamAsync(files[0].Name, stream => client.DownloadFileAsync(files[0].Path, stream));
            if (saved is not null)
                _dialogs.ShowToast($"Uloženo do {saved}.", ToastKind.Success);
            return;
        }

        string? folder = await _dialogs.PickFolderAsync($"Vyber složku pro {files.Count} souborů");
        if (folder is null) return;

        int saved2 = 0, skipped = 0;
        foreach (var file in files)
        {
            string local = System.IO.Path.Combine(folder, file.Name);
            if (File.Exists(local)) { skipped++; continue; } // never silently clobber local files
            await using var stream = File.Create(local);
            await client.DownloadFileAsync(file.Path, stream);
            saved2++;
        }
        _dialogs.ShowToast(skipped == 0
            ? $"Staženo {saved2} souborů do {folder}."
            : $"Staženo {saved2}, přeskočeno {skipped} (v cíli už existují).", ToastKind.Success);
    });

    /// <summary>F5 — copies the active panel's selection into the opposite panel's folder.</summary>
    [RelayCommand(CanExecute = nameof(CanTransfer))]
    private Task CopyToOtherPanelAsync() => TransferAsync(move: false);

    /// <summary>F6 — moves the active panel's selection into the opposite panel's folder.</summary>
    [RelayCommand(CanExecute = nameof(CanTransfer))]
    private Task MoveToOtherPanelAsync() => TransferAsync(move: true);

    private Task TransferAsync(bool move) => RunAsync(async () =>
    {
        var source = ActivePanel;
        var target = OppositePanel;
        var selection = source.SelectedItems.ToList();
        if (selection.Count == 0 || !target.HasLoaded) return;
        if (string.Equals(source.CurrentPath, target.CurrentPath, StringComparison.OrdinalIgnoreCase))
        {
            _dialogs.ShowToast("Oba panely zobrazují stejnou složku.", ToastKind.Info);
            return;
        }

        var client = _session.Require();
        string targetDir = target.CurrentPath;
        string verb = move ? "Přesunuto" : "Zkopírováno";
        int done = 0, skipped = 0, failed = 0;
        OverwriteDecision? remembered = null;

        foreach (var item in selection)
        {
            bool overwrite = remembered == OverwriteDecision.OverwriteAll;
            try
            {
                await TransferOneAsync(client, item, targetDir, move, overwrite);
                done++;
            }
            catch (DiffPdfApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Conflict)
            {
                if (item.IsFolder)
                {
                    // Folders are never merged/overwritten server-side — only skip makes sense here.
                    skipped++;
                    _dialogs.ShowToast($"{item.Name}: {ex.Detail ?? "složka v cíli už existuje."}", ToastKind.Info);
                    continue;
                }

                var decision = remembered
                    ?? await AskOverwriteAsync(item.Name, showApplyToAll: selection.Count > 1);
                if (decision is OverwriteDecision.OverwriteAll or OverwriteDecision.SkipAll)
                    remembered = decision;

                if (decision is OverwriteDecision.Overwrite or OverwriteDecision.OverwriteAll)
                {
                    try
                    {
                        await TransferOneAsync(client, item, targetDir, move, overwrite: true);
                        done++;
                    }
                    catch (DiffPdfApiException retryEx)
                    {
                        failed++;
                        _dialogs.ShowToast($"{item.Name}: {retryEx.Detail ?? retryEx.Message}", ToastKind.Error);
                    }
                }
                else
                {
                    skipped++;
                }
            }
            catch (DiffPdfApiException ex)
            {
                failed++;
                _dialogs.ShowToast($"{item.Name}: {ex.Detail ?? ex.Message}", ToastKind.Error);
            }
        }

        var parts = new List<string> { $"{verb}: {done}" };
        if (skipped > 0) parts.Add($"přeskočeno: {skipped}");
        if (failed > 0) parts.Add($"chyb: {failed}");
        _dialogs.ShowToast(string.Join(", ", parts) + ".", failed > 0 ? ToastKind.Error : ToastKind.Success);

        await target.RefreshAsync();
        if (move) await source.RefreshAsync();
    });

    private static Task TransferOneAsync(DiffPdfClient client, FileListItemViewModel item, string targetDir, bool move, bool overwrite) =>
        move
            ? client.MoveFileAsync(new MoveFileRequest(item.Path, targetDir, overwrite))
            : client.CopyFileAsync(new CopyFileRequest(item.Path, targetDir, overwrite));

    // ---------------- helpers ----------------

    /// <summary>Refreshes every panel currently showing <paramref name="directory"/> (both can).</summary>
    private async Task RefreshPanelsShowingAsync(string directory)
    {
        if (LeftPanel.Shows(directory)) await LeftPanel.RefreshAsync();
        if (RightPanel.Shows(directory)) await RightPanel.RefreshAsync();
    }

    private async Task<OverwriteDecision> AskOverwriteAsync(string fileName, bool showApplyToAll)
    {
        var dialog = FileOperationDialogViewModel.ForOverwrite(fileName, showApplyToAll);
        await _dialogs.ShowFileOperationAsync(dialog);
        return dialog.Decision;
    }
}
