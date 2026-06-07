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
        nameof(InProgress), nameof(ProgressText), nameof(HasVerdict), nameof(VerdictText), nameof(VerdictBrush))]
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
        JobStatus.Running => Blue, JobStatus.Completed => Green, JobStatus.Failed => Red,
        JobStatus.Paused => Amber, JobStatus.Cancelled => Gray, _ => Muted,
    };

    public double Progress => Job.Progress;
    public bool InProgress => Job.Status is JobStatus.Running or JobStatus.Paused;
    public string ProgressText => Job.Status.ToString() is "Completed" ? "100 %" : $"{Job.Progress:P0}";

    /// <summary>A finished job shows a verdict; an in-flight one shows nothing (the progress speaks for it).</summary>
    public bool HasVerdict => Job.Status is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled;

    public string VerdictText => Job.Status switch
    {
        JobStatus.Failed => "✗ Selhalo",
        JobStatus.Cancelled => "⊘ Zrušeno",
        JobStatus.Completed when Job.GatePassed == true => "✓ Prošlo",
        JobStatus.Completed when (Job.Differing ?? 0) > 0 => $"✗ {Job.Differing} odlišných",
        JobStatus.Completed when (Job.Errors ?? 0) > 0 => $"⚠ {Job.Errors} chyb",
        JobStatus.Completed => Job.GatePassed == false ? "✗ Neprošlo" : "✓ Hotovo",
        _ => "",
    };

    public IBrush VerdictBrush => Job.Status switch
    {
        JobStatus.Completed when Job.GatePassed == true => Green,
        JobStatus.Completed when (Job.Differing ?? 0) == 0 && (Job.Errors ?? 0) > 0 => Amber,
        JobStatus.Completed => Red,
        JobStatus.Failed => Red,
        JobStatus.Cancelled => Gray,
        _ => Muted,
    };

    public void Apply(JobSummary updated) => Job = updated;

    private static readonly IBrush Blue = new SolidColorBrush(Color.Parse("#9CDCFE"));
    private static readonly IBrush Green = new SolidColorBrush(Color.Parse("#6FCF73"));
    private static readonly IBrush Red = new SolidColorBrush(Color.Parse("#E06C75"));
    private static readonly IBrush Amber = new SolidColorBrush(Color.Parse("#D8A657"));
    private static readonly IBrush Gray = new SolidColorBrush(Color.Parse("#9A9A9A"));
    private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#7A7A7A"));
}
