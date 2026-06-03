using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffPdf.Client;
using DiffPdf.DesktopUI.Services;

namespace DiffPdf.DesktopUI.ViewModels;

/// <summary>Ad-hoc single-pair comparison (synchronous), with the full options editor. Shows the raw JSON result.</summary>
public partial class SingleCompareViewModel : PageViewModel
{
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    private readonly ServerSession _session;

    public override string Title => "Single compare";
    public override int NavOrder => 7;

    public ComparisonOptionsViewModel Options { get; } = new();

    [ObservableProperty] private string _oldPath = string.Empty;
    [ObservableProperty] private string _newPath = string.Empty;
    [ObservableProperty] private string? _resultJson;

    public SingleCompareViewModel(ServerSession session) => _session = session;

    [RelayCommand]
    private Task CompareAsync() => RunAsync(async () =>
    {
        var element = await _session.Require().CompareSingleAsync(new SingleComparisonRequest
        {
            OldPath = OldPath,
            NewPath = NewPath,
            Options = Options.ToOptions(),
        });
        ResultJson = JsonSerializer.Serialize(element, Pretty);
    });
}
