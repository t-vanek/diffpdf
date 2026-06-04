using CommunityToolkit.Mvvm.ComponentModel;
using DiffPdf.Client;

namespace DiffPdf.DesktopUI.ViewModels;

/// <summary>
/// One branch row in the Branches grid: the branch plus its live run-queue snapshot. The snapshot (pushed
/// from the server) drives which per-row action buttons are enabled.
/// </summary>
public partial class BranchRowViewModel(Branch branch) : ObservableObject
{
    public Branch Branch { get; } = branch;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPause), nameof(CanResume), nameof(CanStop), nameof(QueuePaused), nameof(StatusText))]
    private BranchQueueState? _queue;

    public bool QueuePaused => Queue?.QueuePaused ?? false;

    private int Active => Queue is { } q ? q.Pending + q.Queued + q.Running + q.Paused : 0;

    public bool CanRun => true;
    public bool CanEnqueue => true;
    public bool CanPause => !QueuePaused && (Queue?.Running ?? 0) > 0;
    public bool CanResume => QueuePaused || (Queue?.Paused ?? 0) > 0;
    public bool CanStop => Active > 0;

    public string StatusText
    {
        get
        {
            if (Queue is not { } q) return string.Empty;
            string hold = q.QueuePaused ? "Fronta pozastavena · " : string.Empty;
            string paused = q.Paused > 0 ? $", pozastaveno {q.Paused}" : string.Empty;
            return $"{hold}běží {q.Running}, čeká {q.Pending + q.Queued}{paused}";
        }
    }

    public void Apply(BranchQueueState? state) => Queue = state;
}
