using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffPdf.DesktopUI.Services;

namespace DiffPdf.DesktopUI.ViewModels;

/// <summary>Shell: connection bar + left nav of section pages + the active page's content.</summary>
public partial class MainViewModel : ViewModelBase
{
    private readonly ServerSession _session;
    private readonly JobProgressHubClient _hub;

    public IReadOnlyList<PageViewModel> Pages { get; }

    [ObservableProperty] private PageViewModel? _selectedPage;
    [ObservableProperty] private string _serverUrl = "http://localhost:5275";
    [ObservableProperty] private string _clientId = string.Empty;
    [ObservableProperty] private string _clientSecret = string.Empty;
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private string _connectionStatus = "Nepřipojeno";

    public MainViewModel(ServerSession session, JobProgressHubClient hub, NavigationService navigation, IEnumerable<PageViewModel> pages)
    {
        _session = session;
        _hub = hub;
        Pages = pages.OrderBy(p => p.NavOrder).ToList();
        navigation.Navigated += page => SelectedPage = page;
    }

    [RelayCommand]
    private Task ConnectAsync() => RunAsync(async () =>
    {
        await _session.ConnectAsync(
            ServerUrl,
            string.IsNullOrWhiteSpace(ClientId) ? null : ClientId,
            string.IsNullOrWhiteSpace(ClientSecret) ? null : ClientSecret);

        IsConnected = true;
        ConnectionStatus = $"Připojeno: {ServerUrl}";

        try { await _hub.EnsureStartedAsync(); } catch { /* live progress is best-effort */ }

        SelectedPage ??= Pages.FirstOrDefault();
        if (SelectedPage is not null)
            await SelectedPage.ActivateAsync();
    });

    [RelayCommand]
    private Task DisconnectAsync() => RunAsync(async () =>
    {
        await _hub.StopAsync();
        _session.Disconnect();
        IsConnected = false;
        ConnectionStatus = "Nepřipojeno";
    });

    partial void OnSelectedPageChanged(PageViewModel? value)
    {
        if (value is not null && IsConnected)
            _ = value.ActivateAsync();
    }
}
