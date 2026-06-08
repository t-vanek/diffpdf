using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using DiffPdf.DesktopUI.ViewModels;

namespace DiffPdf.DesktopUI.Views;

public partial class SingleCompareView : UserControl
{
    public SingleCompareView()
    {
        InitializeComponent();
        // Accept dropped PDFs anywhere on the page; route to the field under the pointer (else the first empty one).
        AddHandler(DragDrop.DragOverEvent, OnDragOver, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(DragDrop.DropEvent, OnDrop, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private static void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer is { } dt && dt.Contains(DataFormat.File)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        if (DataContext is not SingleCompareViewModel vm || FirstPdfPath(e) is not { } path)
            return;

        var box = (e.Source as Visual)?.FindAncestorOfType<TextBox>();
        if (box?.Name == "NewPathBox") vm.NewPath = path;
        else if (box?.Name == "OldPathBox") vm.OldPath = path;
        else if (string.IsNullOrWhiteSpace(vm.OldPath)) vm.OldPath = path;
        else vm.NewPath = path;
    }

    /// <summary>The local path of the first dropped <c>.pdf</c>, or null if none of the dropped items is a PDF.</summary>
    private static string? FirstPdfPath(DragEventArgs e)
    {
        if (e.DataTransfer is not { } dt || dt.TryGetFiles() is not { } files)
            return null;
        foreach (var item in files)
            if (item is IStorageFile file)
            {
                var path = file.Path.LocalPath;
                if (path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) return path;
            }
        return null;
    }
}
