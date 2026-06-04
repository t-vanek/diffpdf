using CommunityToolkit.Mvvm.ComponentModel;
using DiffPdf.Client;

namespace DiffPdf.DesktopUI.ViewModels;

/// <summary>
/// One instance row in the Instances grid: the instance plus its live run-queue status. The status (pushed
/// from the server) drives which per-row action buttons are enabled — the UI holds no queue logic itself.
/// </summary>
public partial class InstanceRowViewModel(Instance instance) : ObservableObject
{
    public Instance Instance { get; } = instance;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRun), nameof(CanEnqueue), nameof(CanPause), nameof(CanResume), nameof(CanStop), nameof(StatusText))]
    private InstanceQueueStatus _status = InstanceQueueStatus.Idle;

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
