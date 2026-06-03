using CommunityToolkit.Mvvm.ComponentModel;
using DiffPdf.Client;

namespace DiffPdf.DesktopUI.ViewModels;

/// <summary>Reusable editor model for an optional <see cref="BatchGate"/> (CI pass/fail thresholds).</summary>
public partial class BatchGateViewModel : ObservableObject
{
    /// <summary>When false, no gate is applied (ToGate returns null).</summary>
    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private bool _failOnAnyDifference;
    [ObservableProperty] private decimal? _maxDifferingFiles;
    [ObservableProperty] private decimal? _maxErrors;
    [ObservableProperty] private decimal? _maxFilesWithContentErrors;

    public BatchGate? ToGate() => Enabled
        ? new BatchGate
        {
            FailOnAnyDifference = FailOnAnyDifference,
            MaxDifferingFiles = MaxDifferingFiles is { } a ? (int)a : null,
            MaxErrors = MaxErrors is { } b ? (int)b : null,
            MaxFilesWithContentErrors = MaxFilesWithContentErrors is { } c ? (int)c : null,
        }
        : null;

    public void Load(BatchGate? g)
    {
        Enabled = g is not null;
        FailOnAnyDifference = g?.FailOnAnyDifference ?? false;
        MaxDifferingFiles = g?.MaxDifferingFiles;
        MaxErrors = g?.MaxErrors;
        MaxFilesWithContentErrors = g?.MaxFilesWithContentErrors;
    }
}
