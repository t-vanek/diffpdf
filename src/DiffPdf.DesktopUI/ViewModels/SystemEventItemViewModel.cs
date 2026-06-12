using Avalonia.Media;
using DiffPdf.Client;

namespace DiffPdf.DesktopUI.ViewModels;

/// <summary>Jedna položka centra notifikací: ikona/barva podle závažnosti, zpráva, rozsah a čas.</summary>
public sealed class SystemEventItemViewModel(SystemEvent evt)
{
    public SystemEvent Event { get; } = evt;

    public string Icon => Event.Severity switch
    {
        SystemEventSeverity.Error => "✗",
        SystemEventSeverity.Warning => "⚠",
        _ => "•",
    };

    public IBrush Brush => Event.Severity switch
    {
        SystemEventSeverity.Error => Palette.Bad,
        SystemEventSeverity.Warning => Palette.Warning,
        _ => Palette.Info,
    };

    public string Message => Event.Message;

    public string Scope => (Event.BranchKey, Event.InstanceKey) switch
    {
        (null or "", null or "") => "",
        ({ } b, null or "") => b,
        (null or "", { } i) => i,
        var (b, i) => $"{b}/{i}",
    };

    public bool HasDetail => !string.IsNullOrWhiteSpace(Event.Detail);
    public string? Detail => Event.Detail;
}
