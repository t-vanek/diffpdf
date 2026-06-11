using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform; // ClipboardExtensions.SetTextAsync (Avalonia 12 moved it off IClipboard)
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace DiffPdf.DesktopUI.Services;

/// <summary>A view-model hosted in a modal window: supplies the window title and can ask to close.</summary>
public interface IModalViewModel
{
    string WindowTitle { get; }
    event Action? CloseRequested;
}

/// <summary>Owner-window-bound helpers for file dialogs (e.g. saving a downloaded diff artifact) and confirmations.</summary>
public sealed class DialogService
{
    private readonly ToastService _toasts;

    public DialogService(ToastService toasts) => _toasts = toasts;

    /// <summary>Set by the shell once the main window exists.</summary>
    public Window? Owner { get; set; }

    /// <summary>Raises a transient toast notification (bottom-right of the shell).</summary>
    public void ShowToast(string message, ToastKind kind = ToastKind.Info) => _toasts.Show(message, kind);

    /// <summary>Copies <paramref name="text"/> to the OS clipboard via the shell window, with a confirmation toast.</summary>
    public async Task CopyToClipboardAsync(string text, string toast = "Zkopírováno do schránky.")
    {
        if (Owner?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
            ShowToast(toast, ToastKind.Success);
        }
    }

    /// <summary>Modal yes/no confirmation. Returns true only if the user confirms.</summary>
    public async Task<bool> ConfirmAsync(string title, string message)
    {
        if (Owner is null) return false;

        var result = false;
        var dialog = new Window
        {
            Title = title,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };

        var yes = new Button { Content = "Ano", MinWidth = 80, IsDefault = true };
        var no = new Button { Content = "Ne", MinWidth = 80, IsCancel = true };
        yes.Click += (_, _) => { result = true; dialog.Close(); };
        no.Click += (_, _) => { result = false; dialog.Close(); };

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { yes, no },
                },
            },
        };

        await dialog.ShowDialog(Owner);
        return result;
    }

    /// <summary>Opens the per-scope (branch / instance) trigger + comparer settings as a modal dialog.</summary>
    public Task ShowScopeSettingsAsync(ViewModels.ScopeSettingsViewModel vm) =>
        ShowModalAsync(vm, new Views.ScopeSettingsView { DataContext = vm }, width: 760, height: 680);

    /// <summary>Opens the create/edit branch form as a modal dialog. Returns true if the branch was saved.</summary>
    public async Task<bool> ShowBranchFormAsync(ViewModels.BranchFormViewModel vm)
    {
        await ShowModalAsync(vm, new Views.BranchFormView { DataContext = vm }, width: 460, height: null);
        return vm.Saved;
    }

    /// <summary>Opens the create/edit instance form as a modal dialog. Returns true if the instance was saved.</summary>
    public async Task<bool> ShowInstanceFormAsync(ViewModels.InstanceFormViewModel vm)
    {
        await ShowModalAsync(vm, new Views.InstanceFormView { DataContext = vm }, width: 520, height: null);
        return vm.Saved;
    }

    /// <summary>Hosts a modal view-model in a centred owner-modal window, wiring its close request. A fixed
    /// <paramref name="height"/> makes the window resizable; null sizes it to its content (fixed forms).</summary>
    private async Task ShowModalAsync(IModalViewModel vm, Control content, double width, double? height)
    {
        if (Owner is null) return;

        var dialog = new Window
        {
            Title = vm.WindowTitle,
            Width = width,
            CanResize = height is not null,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = content,
        };
        if (height is { } h) dialog.Height = h;
        else dialog.SizeToContent = SizeToContent.Height;

        void OnClose() => dialog.Close();
        vm.CloseRequested += OnClose;
        try { await dialog.ShowDialog(Owner); }
        finally { vm.CloseRequested -= OnClose; }
    }

    /// <summary>Opens a PDF file picker and returns the chosen file's local path, or null if cancelled.</summary>
    public async Task<string?> OpenPdfAsync(string title)
    {
        if (Owner?.StorageProvider is not { } storage)
            return null;

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("PDF") { Patterns = ["*.pdf"] }],
        });
        return files.Count > 0 ? files[0].Path.LocalPath : null;
    }

    /// <summary>Opens a multi-select PDF picker and returns the chosen local paths (empty if cancelled).</summary>
    public async Task<IReadOnlyList<string>> OpenPdfsAsync(string title)
    {
        if (Owner?.StorageProvider is not { } storage)
            return [];

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType("PDF") { Patterns = ["*.pdf"] }],
        });
        return files.Select(f => f.Path.LocalPath).ToList();
    }

    /// <summary>Opens the create-folder / rename / overwrite dialog; inspect the view-model's Confirmed afterwards.</summary>
    public Task ShowFileOperationAsync(ViewModels.FileOperationDialogViewModel vm) =>
        ShowModalAsync(vm, new Views.FileOperationDialogView { DataContext = vm }, width: 420, height: null);

    /// <summary>Opens a folder picker and returns the chosen local folder path, or null if cancelled.</summary>
    public async Task<string?> PickFolderAsync(string title)
    {
        if (Owner?.StorageProvider is not { } storage)
            return null;

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return folders.Count > 0 ? folders[0].Path.LocalPath : null;
    }

    /// <summary>
    /// Prompts for a save location and streams into it via <paramref name="write"/> (no whole-file buffering).
    /// Returns the saved path, or null if cancelled.
    /// </summary>
    public async Task<string?> SaveStreamAsync(string suggestedName, Func<Stream, Task> write)
    {
        if (Owner?.StorageProvider is not { } storage)
            return null;

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = suggestedName,
            DefaultExtension = "pdf",
            FileTypeChoices = [new FilePickerFileType("PDF") { Patterns = ["*.pdf"] }],
        });
        if (file is null)
            return null;

        await using var stream = await file.OpenWriteAsync();
        await write(stream);
        return file.Path.LocalPath;
    }

    /// <summary>Prompts for a save location and writes <paramref name="content"/>. Returns the saved path, or null if cancelled.</summary>
    public async Task<string?> SaveBytesAsync(string suggestedName, byte[] content)
    {
        if (Owner?.StorageProvider is not { } storage)
            return null;

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = suggestedName,
            DefaultExtension = "pdf",
            FileTypeChoices = [new FilePickerFileType("PDF") { Patterns = ["*.pdf"] }],
        });
        if (file is null)
            return null;

        await using var stream = await file.OpenWriteAsync();
        await stream.WriteAsync(content);
        return file.Path.LocalPath;
    }

    /// <summary>Opens a file pair's full detail — including the inline diff PDF preview — in a standalone,
    /// non-modal window (owned by the main window).</summary>
    public void ShowFileDetail(ViewModels.FilePairDetailViewModel vm)
    {
        if (Owner is null) return;
        new Views.FilePairDetailWindow { DataContext = vm }.Show(Owner);
    }
}
