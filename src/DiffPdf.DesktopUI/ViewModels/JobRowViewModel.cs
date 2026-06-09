using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using DiffPdf.Client;

namespace DiffPdf.DesktopUI.ViewModels;

/// <summary>
/// One job row in the Úlohy grid: a coloured status chip, progress, and a quick outcome verdict (✓ Prošlo /
/// ✗ N odlišných / ⚠ chyby) derived from the enriched <see cref="JobSummary"/>. Settable so a realtime event
/// can update the row in place (preserving selection).
/// </summary>
public partial class JobRowViewModel(JobSummary job) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(StatusIcon), nameof(StatusBrush), nameof(Progress),
        nameof(InProgress), nameof(ProgressText), nameof(HasVerdict), nameof(VerdictText), nameof(VerdictBrush),
        nameof(WasRecovered))]
    private JobSummary _job = job;

    public Guid Id => Job.Id;

    public string StatusIcon => Job.Status switch
    {
        JobStatus.Running => "▶", JobStatus.Completed => "✓", JobStatus.Failed => "✗",
        JobStatus.Cancelled => "⊘", JobStatus.Paused => "⏸", JobStatus.Queued => "⏳", _ => "•",
    };

    public string StatusText => Job.Status switch
    {
        JobStatus.Running => "Běží", JobStatus.Completed => "Hotovo", JobStatus.Failed => "Selhalo",
        JobStatus.Cancelled => "Zrušeno", JobStatus.Paused => "Pozastaveno", JobStatus.Queued => "Ve frontě",
        JobStatus.Draft => "Koncept", _ => Job.Status.ToString(),
    };

    public IBrush StatusBrush => Job.Status switch
    {
        JobStatus.Running => Palette.Info, JobStatus.Completed => Palette.Good, JobStatus.Failed => Palette.Bad,
        JobStatus.Paused => Palette.Paused, JobStatus.Cancelled => Palette.Skipped, _ => Palette.Muted,
    };

    public double Progress => Job.Progress;
    public bool InProgress => Job.Status is JobStatus.Running or JobStatus.Paused;
    public string ProgressText => Job.Status is JobStatus.Completed ? "100 %" : $"{Job.Progress:P0}";

    /// <summary>A finished job shows a verdict; an in-flight one shows nothing (the progress speaks for it).</summary>
    public bool HasVerdict => Job.Status is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled;

    public string VerdictText => Job.Status switch
    {
        JobStatus.Failed => "✗ Selhalo",
        JobStatus.Cancelled => "⊘ Zrušeno",
        JobStatus.Completed when Job.GatePassed == true => "✓ Prošlo",
        JobStatus.Completed when (Job.Differing ?? 0) > 0 => $"✗ {Format.Plural(Job.Differing ?? 0, "odlišný", "odlišné", "odlišných")}",
        JobStatus.Completed when (Job.Errors ?? 0) > 0 => $"⚠ {Format.Plural(Job.Errors ?? 0, "chyba", "chyby", "chyb")}",
        JobStatus.Completed => Job.GatePassed == false ? "✗ Neprošlo" : "✓ Hotovo",
        _ => "",
    };

    public IBrush VerdictBrush => Job.Status switch
    {
        JobStatus.Completed when Job.GatePassed == true => Palette.Good,
        JobStatus.Completed when (Job.Differing ?? 0) == 0 && (Job.Errors ?? 0) > 0 => Palette.Warning,
        JobStatus.Completed => Palette.Bad,
        JobStatus.Failed => Palette.Bad,
        JobStatus.Cancelled => Palette.Skipped,
        _ => Palette.Muted,
    };

    /// <summary>The job was auto-recovered after an interruption (crash / restart / stale worker) — shows an "Obnoveno" chip.</summary>
    public bool WasRecovered => Job.RecoveredAt is not null;

    public void Apply(JobSummary updated) => Job = updated;
}
