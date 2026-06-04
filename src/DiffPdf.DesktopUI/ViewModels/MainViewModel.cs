using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffPdf.DesktopUI.Configuration;
using DiffPdf.DesktopUI.Services;

namespace DiffPdf.DesktopUI.ViewModels;

/// <summary>Shell: connection bar + left nav of section pages + the active page's content.</summary>
public partial class MainViewModel : ViewModelBase
{
    private readonly ServerSession _session;
    private readonly JobProgressHubClient _hub;
    private readonly ClientConfig _config;
    private readonly ServerDiscoveryClient _discovery;

    public IReadOnlyList<PageViewModel> Pages { get; }

    [ObservableProperty] private PageViewModel? _selectedPage;
    [ObservableProperty] private string _serverUrl = "http://localhost:5275";
    [ObservableProperty] private string _clientId = string.Empty;
    [ObservableProperty] private string _clientSecret = string.Empty;
    [ObservableProperty] private bool _isConnected;
    // Drives visibility of the ClientId/Secret fields — only shown when the connected server requires auth.
    [ObservableProperty] private bool _authEnabled;
    [ObservableProperty] private string _connectionStatus = "Nepřipojeno";

    public MainViewModel(ServerSession session, JobProgressHubClient hub, NavigationService navigation,
        ClientConfig config, ServerDiscoveryClient discovery, IEnumerable<PageViewModel> pages)
    {
        _session = session;
        _hub = hub;
        _config = config;
        _discovery = discovery;
        Pages = pages.OrderBy(p => p.NavOrder).ToList();
        navigation.Navigated += page => SelectedPage = page;

        // Seed the connection bar from config: explicit URL + optional credentials for an auth server.
        if (!string.IsNullOrWhiteSpace(config.Server.Url))
            ServerUrl = config.Server.Url!.Trim();
        ClientId = config.Auth.ClientId ?? string.Empty;
        ClientSecret = config.Auth.ClientSecret ?? string.Empty;
    }

    /// <summary>Connects automatically on startup: an explicit configured URL wins, otherwise LAN discovery.</summary>
    public Task AutoConnectAsync() => RunAsync(async () =>
    {
        if (!_config.Server.AutoConnect || IsConnected)
            return;

        string? target = string.IsNullOrWhiteSpace(_config.Server.Url) ? null : _config.Server.Url!.Trim();
        if (target is null && _config.Server.Discovery.Enabled)
            target = (await _discovery.DiscoverAsync(_config.Server.Discovery))?.ToString();

        if (string.IsNullOrWhiteSpace(target))
        {
            ConnectionStatus = "Server nenalezen — zadej adresu ručně";
            return;
        }

        ServerUrl = target;
        await ConnectCoreAsync();
    });

    [RelayCommand]
    private Task ConnectAsync() => RunAsync(ConnectCoreAsync);

    private async Task ConnectCoreAsync()
    {
        await _session.ConnectAsync(
            ServerUrl,
            string.IsNullOrWhiteSpace(ClientId) ? null : ClientId,
            string.IsNullOrWhiteSpace(ClientSecret) ? null : ClientSecret);

        IsConnected = true;
        AuthEnabled = _session.ServerRequiresAuth;
        ConnectionStatus = $"Připojeno: {ServerUrl}";

        try { await _hub.EnsureStartedAsync(); } catch { /* live progress is best-effort */ }

        SelectedPage ??= Pages.FirstOrDefault();
        if (SelectedPage is not null)
            await SelectedPage.ActivateAsync();
    }

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
