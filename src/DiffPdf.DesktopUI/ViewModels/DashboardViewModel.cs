using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffPdf.Client;
using DiffPdf.DesktopUI.Services;

namespace DiffPdf.DesktopUI.ViewModels;

/// <summary>Přehled: obecný technický stav systému a prostředí (bez per-větev / per-instance statistik).</summary>
public partial class DashboardViewModel : PageViewModel
{
    private readonly ServerSession _session;
    private readonly DispatcherTimer _timer;

    public override string Title => "Přehled";
    public override int NavOrder => 0;

    [ObservableProperty] private bool? _healthy;
    [ObservableProperty] private OperationalStatusResponse? _status;
    [ObservableProperty] private ReadinessResponse? _readiness;
    [ObservableProperty] private bool _autoRefresh;

    public DashboardViewModel(ServerSession session)
    {
        _session = session;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _timer.Tick += (_, _) => { if (!IsBusy) _ = RefreshAsync(); };
    }

    public override Task ActivateAsync() => RefreshAsync();

    [RelayCommand]
    private Task RefreshAsync() => RunAsync(async () =>
    {
        var c = _session.Require();
        Healthy = await c.HealthAsync();
        Status = await c.GetStatusAsync();
        Readiness = await c.GetReadinessAsync();
    });

    partial void OnAutoRefreshChanged(bool value)
    {
        if (value) _timer.Start();
        else _timer.Stop();
    }
}
