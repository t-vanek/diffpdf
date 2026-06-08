using Avalonia;
using Avalonia.Controls;
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
    /// <summary>Set by the shell once the main window exists.</summary>
    public Window? Owner { get; set; }

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
}
