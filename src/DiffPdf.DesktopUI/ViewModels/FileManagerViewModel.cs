using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffPdf.Client;
using DiffPdf.DesktopUI.Services;

namespace DiffPdf.DesktopUI.ViewModels;

/// <summary>
/// Správa souborů: two-panel file manager (simple Total Commander) bridging the user's computer and the
/// server's managed PDF storage. By default the LEFT panel shows the local machine (drive list →
/// folders) and the RIGHT panel the server root; each panel's header can switch sides. F5/F6 copy/
/// move the selection into the opposite panel whatever the combination — same-backend operations go
/// through that backend directly, cross-backend ones stream through the transfer queue (upload /
/// download with per-file progress). Pure file management — comparisons are launched elsewhere.
/// </summary>
public partial class FileManagerViewModel : PageViewModel
{
    private readonly DialogService _dialogs;
    private readonly ClientSettingsStore _settings;
    private readonly LocalFileBackend _localBackend = new();
    private readonly ServerFileBackend _serverBackend;
    private bool _initialised;
    private bool _stateRestoreDone;

    public override string Title => "Správa souborů";
    public override string Icon => "🗂";
    public override int NavOrder => 5;

    public FilePanelViewModel LeftPanel { get; }
    public FilePanelViewModel RightPanel { get; }
    public TransferQueueViewModel TransferQueue { get; }

    /// <summary>Raised only by the explicit Tab switch (not by click-activation) — the view moves keyboard focus.</summary>
    public event Action<FilePanelViewModel>? PanelSwitchRequested;

    [ObservableProperty] private FilePanelViewModel _activePanel;

    /// <summary>The transfer target: the panel that is not active.</summary>
    public FilePanelViewModel OppositePanel => ActivePanel == LeftPanel ? RightPanel : LeftPanel;

    public FileManagerViewModel(ServerSession session, DialogService dialogs, ClientSettingsStore settings)
    {
        _dialogs = dialogs;
        _settings = settings;
        _serverBackend = new ServerFileBackend(session);

        // Default layout per the spec: my computer on the left, the server on the right.
        LeftPanel = new FilePanelViewModel(_localBackend);
        RightPanel = new FilePanelViewModel(_serverBackend);
        _activePanel = LeftPanel;
        LeftPanel.IsActive = true;

        foreach (var panel in new[] { LeftPanel, RightPanel })
        {
            panel.Activated += p => ActivePanel = p;
            panel.SelectionChanged += RefreshCommandStates;
            panel.FilesDropped += (p, paths) => EnqueueLocalFiles(p, paths);
            panel.BackendSwitchRequested += (p, kind) => _ = SwitchPanelBackendAsync(p, kind);
            // Persist the layout on every navigation/side switch, so the next start reopens both panels
            // where the user left them.
            panel.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(FilePanelViewModel.CurrentPath)) PersistPanelState();
            };
        }

        TransferQueue = new TransferQueueViewModel
        {
            // The conflict question must not stall the queue invisibly — it pops the modal overwrite dialog
            // ("apply to all" offered whenever more files still wait, so one click settles the batch).
            OverwriteResolver = item => AskOverwriteAsync(
                item.FileName, showApplyToAll: TransferQueue!.Items.Any(i => i != item && i.State == TransferState.Waiting)),
        };
        TransferQueue.BatchCompleted += changed =>
        {
            foreach (var (backend, directory) in changed) _ = RefreshPanelsShowingAsync(backend, directory);
        };
    }

    public override async Task ActivateAsync()
    {
        if (_initialised) return;
        _initialised = true;
        var saved = _settings.LoadFileManagerPanels();
        await Task.WhenAll(
            RestorePanelAsync(LeftPanel, saved?.Left),
            RestorePanelAsync(RightPanel, saved?.Right));
        _stateRestoreDone = true; // only now start persisting, so the restore itself can't clobber the saved state
        PersistPanelState();
    }

    /// <summary>Reopens a panel where it stood last time; a vanished location falls back to the backend's default, quietly.</summary>
    private async Task RestorePanelAsync(FilePanelViewModel panel, FilePanelState? state)
    {
        if (state is null)
        {
            await panel.LoadAsync(panel.Backend.DefaultPath);
            return;
        }

        IFileBackend backend = Enum.TryParse<BackendKind>(state.Backend, out var kind) && kind == BackendKind.Server
            ? _serverBackend
            : _localBackend;
        string target = await CanListAsync(backend, state.Path) ? state.Path : backend.DefaultPath;

        if (backend != panel.Backend) await panel.SetBackendAsync(backend, target);
        else await panel.LoadAsync(target);
    }

    /// <summary>Probe before restoring — an unplugged drive or deleted folder must not greet the user with an error.</summary>
    private static async Task<bool> CanListAsync(IFileBackend backend, string path)
    {
        try
        {
            await backend.ListAsync(path);
            return true;
        }
        catch
        {
            return false; // missing / offline / unauthorized — any reason means "use the default"
        }
    }

    private void PersistPanelState()
    {
        if (!_stateRestoreDone) return;
        _settings.SaveFileManagerPanels(new FileManagerPanelsState(
            new FilePanelState(LeftPanel.Backend.Kind.ToString(), LeftPanel.CurrentPath),
            new FilePanelState(RightPanel.Backend.Kind.ToString(), RightPanel.CurrentPath)));
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

    private Task SwitchPanelBackendAsync(FilePanelViewModel panel, BackendKind kind) =>
        panel.SetBackendAsync(kind == BackendKind.Local ? _localBackend : _serverBackend);

    // ---------------- toolbar actions (target the active panel) ----------------

    private bool HasSelection => ActivePanel.SelectedItems.Count > 0;
    private bool HasSingleSelection => ActivePanel.PrimaryItem is not null;
    private bool HasServerFileSelection => ActivePanel.IsServer && ActivePanel.SelectedItems.Any(i => !i.IsFolder);
    private bool CanTransfer => HasSelection && OppositePanel.HasLoaded;

    private void RefreshCommandStates()
    {
        RenameCommand.NotifyCanExecuteChanged();
        CopyPathCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        DownloadCommand.NotifyCanExecuteChanged();
        CopyToOtherPanelCommand.NotifyCanExecuteChanged();
        MoveToOtherPanelCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private async Task UploadFilesAsync()
    {
        var paths = await _dialogs.OpenPdfsAsync("Vyber PDF k přidání do aktivního panelu");
        if (paths.Count > 0)
            EnqueueLocalFiles(ActivePanel, paths);
    }

    /// <summary>Picker / OS-drop entry: local PDFs head into the given panel's current folder.</summary>
    private void EnqueueLocalFiles(FilePanelViewModel target, IReadOnlyList<string> localPaths)
    {
        if (!target.HasLoaded || !target.Backend.CanWriteTo(target.CurrentPath))
        {
            _dialogs.ShowToast("Sem nelze soubory vložit — otevři nejdřív složku.", ToastKind.Info);
            return;
        }
        TransferQueue.Enqueue(localPaths.Select(path => new TransferRequest(
            _localBackend, path, Path.GetFileName(path), TryFileLength(path),
            target.Backend, target.CurrentPath, Move: false)));
    }

    private static long? TryFileLength(string path)
    {
        try { return new FileInfo(path).Length; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    [RelayCommand]
    private Task CreateFolderAsync() => RunAsync(async () =>
    {
        if (!ActivePanel.Backend.CanWriteTo(ActivePanel.CurrentPath))
        {
            _dialogs.ShowToast("Na úrovni disků nelze vytvořit složku — otevři nejdřív disk.", ToastKind.Info);
            return;
        }

        var dialog = FileOperationDialogViewModel.ForCreateFolder();
        await _dialogs.ShowFileOperationAsync(dialog);
        if (!dialog.Confirmed) return;

        var created = await ActivePanel.Backend.CreateFolderAsync(ActivePanel.CurrentPath, dialog.ResultName);
        _dialogs.ShowToast($"Složka „{created.Name}“ vytvořena.", ToastKind.Success);

        await RefreshPanelsShowingAsync(ActivePanel.Backend, ActivePanel.CurrentPath);
        ActivePanel.SelectByPath(created.Path);
    });

    [RelayCommand(CanExecute = nameof(HasSingleSelection))]
    private Task RenameAsync() => RunAsync(async () =>
    {
        if (ActivePanel.PrimaryItem is not { } item) return;

        var dialog = FileOperationDialogViewModel.ForRename(item.Name, isFile: !item.IsFolder);
        await _dialogs.ShowFileOperationAsync(dialog);
        if (!dialog.Confirmed || dialog.ResultName == item.Name) return;

        var renamed = await ActivePanel.Backend.RenameAsync(item.Path, dialog.ResultName);
        await RefreshPanelsShowingAsync(ActivePanel.Backend, ActivePanel.CurrentPath);
        ActivePanel.SelectByPath(renamed.Path);
    });

    /// <summary>Zkopíruje úplnou cestu označené položky aktivního panelu (kontextové menu) —
    /// tester ji vkládá do hlášení nebo do konfigurace instance.</summary>
    [RelayCommand(CanExecute = nameof(HasSingleSelection))]
    private Task CopyPathAsync() =>
        ActivePanel.PrimaryItem is { } item ? _dialogs.CopyToClipboardAsync(item.Path, "Cesta zkopírována.") : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task DeleteAsync() => RunAsync(async () =>
    {
        var selection = ActivePanel.SelectedItems.ToList();
        if (selection.Count == 0) return;

        string what = selection.Count == 1
            ? $"„{selection[0].Name}“"
            : Format.Plural(selection.Count, "vybranou položku", "vybrané položky", "vybraných položek");
        string where = ActivePanel.IsServer ? "na serveru" : "na tomto počítači (do koše)";
        if (!await _dialogs.ConfirmAsync("Smazat", $"Smaže se {what} {where}.", confirmText: "Smazat", danger: true))
            return;

        var backend = ActivePanel.Backend;
        var nonEmptyFolders = new List<FileListItemViewModel>();
        int deleted = 0, failed = 0;

        foreach (var item in selection)
        {
            try
            {
                await backend.DeleteAsync(item.Path, recursive: false);
                deleted++;
            }
            catch (FileBackendConflictException) when (item.IsFolder)
            {
                nonEmptyFolders.Add(item); // ask once for all of them below
            }
            catch (Exception ex) when (ex is DiffPdfApiException or IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                failed++;
                _dialogs.ShowToast($"{item.Name}: {(ex as DiffPdfApiException)?.Detail ?? ex.Message}", ToastKind.Error);
            }
        }

        // Non-empty folders need an explicit, scarier confirmation — they may hold content the
        // PDF-only listing does not show.
        if (nonEmptyFolders.Count > 0)
        {
            string folders = string.Join(", ", nonEmptyFolders.Select(f => $"„{f.Name}“"));
            if (await _dialogs.ConfirmAsync(
                    "Složka není prázdná",
                    $"{folders}: složka obsahuje další položky (i takové, které tento seznam nezobrazuje).",
                    confirmText: "Smazat včetně obsahu", cancelText: "Přeskočit", danger: true))
            {
                foreach (var folder in nonEmptyFolders)
                {
                    try
                    {
                        await backend.DeleteAsync(folder.Path, recursive: true);
                        deleted++;
                    }
                    catch (Exception ex) when (ex is DiffPdfApiException or IOException or UnauthorizedAccessException)
                    {
                        failed++;
                        _dialogs.ShowToast($"{folder.Name}: {(ex as DiffPdfApiException)?.Detail ?? ex.Message}", ToastKind.Error);
                    }
                }
            }
        }

        if (deleted > 0)
            _dialogs.ShowToast(failed == 0
                ? $"Smazáno: {deleted}."
                : $"Smazáno: {deleted}, chyb: {failed}.", failed == 0 ? ToastKind.Success : ToastKind.Error);

        await RefreshPanelsShowingAsync(ActivePanel.Backend, ActivePanel.CurrentPath);
    });

    /// <summary>Saves server PDFs to disk (1 file = save dialog, more = folder picker). Local-panel
    /// selections don't need downloading — they are already on this computer.</summary>
    [RelayCommand(CanExecute = nameof(HasServerFileSelection))]
    private Task DownloadAsync() => RunAsync(async () =>
    {
        var files = ActivePanel.SelectedItems.Where(i => !i.IsFolder).ToList();
        if (files.Count == 0 || !ActivePanel.IsServer) return;
        var backend = ActivePanel.Backend;

        if (files.Count == 1)
        {
            string? saved = await _dialogs.SaveStreamAsync(files[0].Name, async stream =>
            {
                await using var source = await backend.OpenReadAsync(files[0].Path);
                await source.CopyToAsync(stream);
            });
            if (saved is not null)
                _dialogs.ShowToast($"Uloženo do {saved}.", ToastKind.Success);
            return;
        }

        string? folder = await _dialogs.PickFolderAsync($"Vyber složku pro {files.Count} souborů");
        if (folder is null) return;

        int saved2 = 0, skipped = 0;
        foreach (var file in files)
        {
            string local = Path.Combine(folder, file.Name);
            if (File.Exists(local)) { skipped++; continue; } // never silently clobber local files
            await using var stream = File.Create(local);
            await using var source = await backend.OpenReadAsync(file.Path);
            await source.CopyToAsync(stream);
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

    private Task TransferAsync(bool move) =>
        RunAsync(() => TransferCoreAsync(ActivePanel, OppositePanel, ActivePanel.SelectedItems.ToList(), move));

    /// <summary>Drag&amp;drop from the other panel: source/target come from the drag, not from the active panel.</summary>
    public Task HandlePanelDropAsync(
        FilePanelViewModel source, FilePanelViewModel target, IReadOnlyList<FileListItemViewModel> items, bool move) =>
        RunAsync(() => TransferCoreAsync(source, target, items.ToList(), move));

    private async Task TransferCoreAsync(
        FilePanelViewModel source, FilePanelViewModel target, List<FileListItemViewModel> selection, bool move)
    {
        if (selection.Count == 0 || !target.HasLoaded) return;
        if (!target.Backend.CanWriteTo(target.CurrentPath))
        {
            _dialogs.ShowToast("Cílový panel je na úrovni disků — otevři v něm nejdřív složku.", ToastKind.Info);
            return;
        }
        if (source.Backend == target.Backend
            && string.Equals(source.CurrentPath, target.CurrentPath, StringComparison.OrdinalIgnoreCase))
        {
            _dialogs.ShowToast("Oba panely zobrazují stejnou složku.", ToastKind.Info);
            return;
        }

        if (source.Backend == target.Backend)
            await SameBackendTransferAsync(source, target, selection, move);
        else
            await CrossBackendTransferAsync(source, target, selection, move);
    }

    /// <summary>Server↔server / local↔local: single backend calls per item (no streaming needed).</summary>
    private async Task SameBackendTransferAsync(
        FilePanelViewModel source, FilePanelViewModel target, List<FileListItemViewModel> selection, bool move)
    {
        var backend = source.Backend;
        string targetDir = target.CurrentPath;
        string verb = move ? "Přesunuto" : "Zkopírováno";
        int done = 0, skipped = 0, failed = 0;
        OverwriteDecision? remembered = null;

        foreach (var item in selection)
        {
            bool overwrite = remembered == OverwriteDecision.OverwriteAll;
            try
            {
                await TransferOneAsync(backend, item.Path, targetDir, move, overwrite);
                done++;
            }
            catch (FileBackendConflictException ex)
            {
                if (item.IsFolder)
                {
                    // Folders are never merged/overwritten — only skip makes sense here.
                    skipped++;
                    _dialogs.ShowToast($"{item.Name}: {ex.Message}", ToastKind.Info);
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
                        await TransferOneAsync(backend, item.Path, targetDir, move, overwrite: true);
                        done++;
                    }
                    catch (Exception retryEx) when (retryEx is DiffPdfApiException or IOException or FileBackendConflictException)
                    {
                        failed++;
                        _dialogs.ShowToast($"{item.Name}: {(retryEx as DiffPdfApiException)?.Detail ?? retryEx.Message}", ToastKind.Error);
                    }
                }
                else
                {
                    skipped++;
                }
            }
            catch (Exception ex) when (ex is DiffPdfApiException or IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                failed++;
                _dialogs.ShowToast($"{item.Name}: {(ex as DiffPdfApiException)?.Detail ?? ex.Message}", ToastKind.Error);
            }
        }

        var parts = new List<string> { $"{verb}: {done}" };
        if (skipped > 0) parts.Add($"přeskočeno: {skipped}");
        if (failed > 0) parts.Add($"chyb: {failed}");
        _dialogs.ShowToast(string.Join(", ", parts) + ".", failed > 0 ? ToastKind.Error : ToastKind.Success);

        await target.RefreshAsync();
        if (move) await source.RefreshAsync();
    }

    private static Task TransferOneAsync(IFileBackend backend, string sourcePath, string targetDir, bool move, bool overwrite) =>
        move
            ? backend.MoveAsync(sourcePath, targetDir, overwrite)
            : backend.CopyAsync(sourcePath, targetDir, overwrite);

    /// <summary>
    /// Local↔server: files stream through the transfer queue (progress + per-file conflicts); folders
    /// are expanded — the structure is created at the target first, then their PDFs are enqueued.
    /// A cross-backend folder MOVE is refused: only the visible PDFs would transfer, so deleting the
    /// source folder afterwards could destroy content the listing hides.
    /// </summary>
    private async Task CrossBackendTransferAsync(
        FilePanelViewModel source, FilePanelViewModel target, List<FileListItemViewModel> selection, bool move)
    {
        var requests = new List<TransferRequest>();
        int refusedFolderMoves = 0, conflictFolders = 0;

        foreach (var item in selection)
        {
            if (item.IsFolder)
            {
                if (move)
                {
                    refusedFolderMoves++;
                    continue;
                }
                conflictFolders += await ExpandFolderAsync(
                    source.Backend, item.Path, item.Name, target.Backend, target.CurrentPath, requests);
            }
            else
            {
                requests.Add(new TransferRequest(
                    source.Backend, item.Path, item.Name, item.Item.SizeBytes,
                    target.Backend, target.CurrentPath, move));
            }
        }

        if (refusedFolderMoves > 0)
            _dialogs.ShowToast(
                $"Přesun {Format.Plural(refusedFolderMoves, "složky", "složek", "složek")} mezi počítačem a serverem není podporován — " +
                "přenesly by se jen PDF a zbytek obsahu by se smazal. Použij kopii.", ToastKind.Error);
        if (conflictFolders > 0)
            _dialogs.ShowToast($"Přeskočeno {Format.Plural(conflictFolders, "složka", "složky", "složek")} — v cíli už existují.", ToastKind.Info);

        if (requests.Count > 0)
            TransferQueue.Enqueue(requests);
        else if (refusedFolderMoves == 0 && conflictFolders == 0)
            _dialogs.ShowToast("Není co přenést.", ToastKind.Info);

        // Created (possibly empty) folder skeletons should show up right away; files follow via the queue.
        if (conflictFolders < selection.Count(i => i.IsFolder) && !move)
            await RefreshPanelsShowingAsync(target.Backend, target.CurrentPath);
    }

    /// <summary>Replicates one folder at the target and queues its PDFs; returns 1 when the folder name collides (skipped).</summary>
    private async Task<int> ExpandFolderAsync(
        IFileBackend source, string sourceFolderPath, string folderName,
        IFileBackend target, string targetDirectory, List<TransferRequest> requests)
    {
        string targetFolderPath;
        try
        {
            targetFolderPath = (await target.CreateFolderAsync(targetDirectory, folderName)).Path;
        }
        catch (FileBackendConflictException)
        {
            return 1; // folders never merge — consistent with same-backend semantics
        }

        var listing = await source.ListAsync(sourceFolderPath);
        int conflicts = 0;
        foreach (var item in listing.Items)
        {
            if (item.Kind == FileItemKind.Folder)
                conflicts += await ExpandFolderAsync(source, item.Path, item.Name, target, targetFolderPath, requests);
            else
                requests.Add(new TransferRequest(source, item.Path, item.Name, item.SizeBytes, target, targetFolderPath, Move: false));
        }
        return conflicts;
    }

    // ---------------- helpers ----------------

    /// <summary>Refreshes every panel currently showing <paramref name="directory"/> on <paramref name="backend"/>.</summary>
    private async Task RefreshPanelsShowingAsync(IFileBackend backend, string directory)
    {
        if (LeftPanel.Backend == backend && LeftPanel.Shows(directory)) await LeftPanel.RefreshAsync();
        if (RightPanel.Backend == backend && RightPanel.Shows(directory)) await RightPanel.RefreshAsync();
    }

    private async Task<OverwriteDecision> AskOverwriteAsync(string fileName, bool showApplyToAll)
    {
        var dialog = FileOperationDialogViewModel.ForOverwrite(fileName, showApplyToAll);
        await _dialogs.ShowFileOperationAsync(dialog);
        return dialog.Decision;
    }
}
