using CommunityToolkit.Mvvm.ComponentModel;
using DiffPdf.Client;

namespace DiffPdf.DesktopUI.ViewModels;

/// <summary>
/// One branch row in the Branches grid: the branch plus its live run-queue snapshot. The snapshot (pushed
/// from the server) drives which per-row action buttons are enabled.
/// </summary>
public partial class BranchRowViewModel(Branch branch) : ObservableObject, ISelectableRow
{
    // Settable so an auto-refresh can update a row's name/enabled in place — keeping the same row object, so
    // the user's selection and checkbox state survive the refresh.
    [ObservableProperty] private Branch _branch = branch;

    /// <summary>Checkbox state for bulk actions (multi-select delete). Independent of the grid's single selection.</summary>
    [ObservableProperty] private bool _isSelected;

    /// <summary>Number of instances under this branch (from the summary list).</summary>
    [ObservableProperty] private int _instanceCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPause), nameof(CanResume), nameof(CanStop), nameof(QueuePaused),
        nameof(StatusText), nameof(IsActive))]
    private BranchQueueState? _queue;

    public bool QueuePaused => Queue?.QueuePaused ?? false;

    private int Active => Queue is { } q ? q.Pending + q.Queued + q.Running + q.Paused : 0;

    public bool CanRun => true;
    public bool CanEnqueue => true;
    public bool CanPause => !QueuePaused && (Queue?.Running ?? 0) > 0;
    public bool CanResume => QueuePaused || (Queue?.Paused ?? 0) > 0;
    public bool CanStop => Active > 0;

    /// <summary>True when the branch has anything queued/running/paused or its queue is held — drives the Fronta accent.</summary>
    public bool IsActive => Active > 0 || QueuePaused;

    public string StatusText
    {
        get
        {
            if (Queue is not { } q) return "—";                              // queue state not loaded yet
            if (Active == 0 && !q.QueuePaused) return "Nečinné";             // loaded, nothing happening
            string hold = q.QueuePaused ? "Fronta pozastavena · " : string.Empty;
            string paused = q.Paused > 0 ? $", pozastaveno {q.Paused}" : string.Empty;
            return $"{hold}běží {q.Running}, čeká {q.Pending + q.Queued}{paused}";
        }
    }

    public void Apply(BranchQueueState? state) => Queue = state;
}
