using Avalonia.Controls;
using Avalonia.Controls.Templates;
using DiffPdf.DesktopUI.ViewModels;

namespace DiffPdf.DesktopUI;

/// <summary>Resolves a View for a ViewModel by naming convention (…ViewModels.XViewModel → …Views.XView).</summary>
public sealed class ViewLocator : IDataTemplate
{
    public Control Build(object? param)
    {
        if (param is null)
            return new TextBlock { Text = "null" };

        var name = param.GetType().FullName!.Replace("ViewModel", "View", StringComparison.Ordinal);
        var type = Type.GetType(name);

        return type is not null
            ? (Control)Activator.CreateInstance(type)!
            : new TextBlock { Text = "View not found: " + name };
    }

    public bool Match(object? data) => data is ViewModelBase;
}
