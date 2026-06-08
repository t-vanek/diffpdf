using System.Linq;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using DiffPdf.Client;
using DiffPdf.DesktopUI.Controls;
using DiffPdf.DesktopUI.Services;

namespace DiffPdf.DesktopUI.ViewModels;

/// <summary>Přehled: obecný technický stav systému a prostředí (bez per-větev / per-instance statistik).</summary>
public partial class DashboardViewModel : PageViewModel
{
    private readonly ServerSession _session;
    private readonly DispatcherTimer _timer;

    public override string Title => "Přehled";
    public override string Icon => "⌂";
    public override int NavOrder => 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OverallText), nameof(OverallBrush), nameof(Rhythm))]
    private bool? _healthy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OverallText), nameof(OverallBrush), nameof(Rhythm))]
    private OperationalStatusResponse? _status;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OverallText), nameof(OverallBrush), nameof(Rhythm))]
    private ReadinessResponse? _readiness;

    // Stable live-count props (updated each refresh). The count-up / stagger animations bind to controls that
    // must PERSIST across the 5 s refresh; the Status ContentControl rebuilds its template each refresh, which
    // would re-trigger the entrance animations — so these counts live in the static part of the page.
    [ObservableProperty] private int _runningJobs;
    [ObservableProperty] private int _queuedJobs;
    [ObservableProperty] private int _pausedJobs;
    [ObservableProperty] private int _activeTasks;
    [ObservableProperty] private int _enabledChecks;

    public DashboardViewModel(ServerSession session)
    {
        _session = session;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _timer.Tick += (_, _) => { if (!IsBusy) _ = RefreshAsync(); };
    }

    // The Přehled polls itself every 5 s while it is the visible page, and stops when navigated away.
    public override Task ActivateAsync()
    {
        _timer.Start();
        return RefreshAsync();
    }

    public override Task DeactivateAsync()
    {
        _timer.Stop();
        return Task.CompletedTask;
    }

    private Task RefreshAsync() => RunAsync(async () =>
    {
        var c = _session.Require();
        Healthy = await c.HealthAsync();
        Status = await c.GetStatusAsync();
        RunningJobs = Status.Backlog.RunningJobs;
        QueuedJobs = Status.Backlog.QueuedJobs;
        PausedJobs = Status.Backlog.PausedJobs;
        ActiveTasks = Status.Backlog.ActiveTasks;
        EnabledChecks = Status.EnabledChecks;
        Readiness = await c.GetReadinessAsync();
    }, toastOnError: false); // periodic auto-refresh: errors show in the hero, don't spam a toast every 5 s

    // ----- Overall health summary (liveness + dependency health + readiness + leader lease) -----

    private bool DependenciesHealthy => Status is { } s
        && s.Dependencies.Database.Ok && s.Dependencies.Renderer.Ok && s.Dependencies.Storage.Ok;
    private bool ReadinessHealthy => Readiness is not { } r || r.Checks.All(c => c.Ok);
    private bool LeaseHealthy => Status is not { } s || s.Leader.LeaseHealthy;

    /// <summary>Drives the hero ECG — maps health to a vital-signs rhythm (waveform, on-screen rate and
    /// scroll speed; colour comes from <see cref="OverallBrush"/>): a calm beat when operational, a faster
    /// "stressed" beat when degraded, a flat line when down, and a slow shallow wander while still resolving.</summary>
    public EcgRhythm Rhythm => Healthy switch
    {
        null => EcgRhythm.Searching,
        false => EcgRhythm.Flatline,
        _ => Status is null ? EcgRhythm.Normal
            : DependenciesHealthy && ReadinessHealthy && LeaseHealthy ? EcgRhythm.Normal : EcgRhythm.Elevated,
    };

    /// <summary>The headline system state shown in the hero banner.</summary>
    public string OverallText => Healthy switch
    {
        null => "Zjišťuji stav…",
        false => "Nedostupné",
        _ => Status is null ? "Připojeno"
            : DependenciesHealthy && ReadinessHealthy && LeaseHealthy ? "Dostupné" : "Zhoršený provoz",
    };

    /// <summary>Hero accent colour: green operational, amber degraded, red down, muted while still loading.</summary>
    public IBrush OverallBrush => Healthy switch
    {
        null => Palette.Muted,
        false => Palette.Bad,
        _ => Status is null ? Palette.Info
            : DependenciesHealthy && ReadinessHealthy && LeaseHealthy ? Palette.Good : Palette.Warning,
    };
}
