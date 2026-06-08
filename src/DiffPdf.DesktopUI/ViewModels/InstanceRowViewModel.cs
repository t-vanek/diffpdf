using CommunityToolkit.Mvvm.ComponentModel;
using DiffPdf.Client;

namespace DiffPdf.DesktopUI.ViewModels;

/// <summary>
/// One instance row in the Instances grid: the instance plus its live run-queue status. The status (pushed
/// from the server) drives which per-row action buttons are enabled — the UI holds no queue logic itself.
/// </summary>
public partial class InstanceRowViewModel(Instance instance) : ObservableObject, ISelectableRow
{
    // Settable so an auto-refresh can update a row's data in place — keeping the same row object, so the
    // user's selection and checkbox state survive the refresh.
    [ObservableProperty] private Instance _instance = instance;

    /// <summary>Checkbox state for bulk actions (multi-select delete). Independent of the grid's single selection.</summary>
    [ObservableProperty] private bool _isSelected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRun), nameof(CanEnqueue), nameof(CanPause), nameof(CanResume), nameof(CanStop),
        nameof(StatusText), nameof(IsActive))]
    private InstanceQueueStatus _status = InstanceQueueStatus.Idle;

    /// <summary>True when the instance is anything but Idle — drives the Fronta accent.</summary>
    public bool IsActive => Status != InstanceQueueStatus.Idle;

    public bool CanRun => Status == InstanceQueueStatus.Idle;
    public bool CanEnqueue => Status == InstanceQueueStatus.Idle;
    public bool CanPause => Status == InstanceQueueStatus.Running;
    public bool CanResume => Status == InstanceQueueStatus.Paused;
    public bool CanStop => Status is InstanceQueueStatus.Pending or InstanceQueueStatus.Queued
        or InstanceQueueStatus.Running or InstanceQueueStatus.Paused;

    public string StatusText => Status switch
    {
        InstanceQueueStatus.Pending => "Ve frontě",
        InstanceQueueStatus.Queued => "Připraveno",
        InstanceQueueStatus.Running => "Běží",
        InstanceQueueStatus.Paused => "Pozastaveno",
        _ => "Nečinné",
    };

    /// <summary>Applies the server-resolved state for this instance (null = idle).</summary>
    public void Apply(InstanceQueueState? state) => Status = state?.Status ?? InstanceQueueStatus.Idle;
}
