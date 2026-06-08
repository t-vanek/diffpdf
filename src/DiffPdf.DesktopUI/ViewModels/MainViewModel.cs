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
    private readonly ClientSettingsStore _settings;

    public IReadOnlyList<PageViewModel> Pages { get; }

    [ObservableProperty] private PageViewModel? _selectedPage;
    [ObservableProperty] private string _serverUrl = "http://localhost:5275";
    [ObservableProperty] private string _clientId = string.Empty;
    [ObservableProperty] private string _clientSecret = string.Empty;
    [ObservableProperty] private bool _isConnected;
    // Drives visibility of the ClientId/Secret fields — only shown when the connected server requires auth.
    [ObservableProperty] private bool _authEnabled;
    [ObservableProperty] private string _connectionStatus = "Nepřipojeno";
    // Persist + auto-connect on next start (the connection gear's "remember" toggle).
    [ObservableProperty] private bool _autoConnect = true;
    [ObservableProperty] private string? _saveNote;

    public MainViewModel(ServerSession session, JobProgressHubClient hub, NavigationService navigation,
        ClientConfig config, ServerDiscoveryClient discovery, ClientSettingsStore settings, IEnumerable<PageViewModel> pages)
    {
        _session = session;
        _hub = hub;
        _config = config;
        _discovery = discovery;
        _settings = settings;
        Pages = pages.OrderBy(p => p.NavOrder).ToList();
        navigation.Navigated += page => SelectedPage = page;

        // Seed the connection settings from config: explicit URL + optional credentials + the auto-connect flag.
        if (!string.IsNullOrWhiteSpace(config.Server.Url))
            ServerUrl = config.Server.Url!.Trim();
        ClientId = config.Auth.ClientId ?? string.Empty;
        ClientSecret = config.Auth.ClientSecret ?? string.Empty;
        AutoConnect = config.Server.AutoConnect;
    }

    /// <summary>Persists the connection settings so the client reconnects automatically on the next start.</summary>
    [RelayCommand]
    private void SaveSettings()
    {
        _settings.Save(ServerUrl, AutoConnect, ClientId, ClientSecret);
        // Mirror into the in-memory config so this session matches what was persisted.
        _config.Server.Url = string.IsNullOrWhiteSpace(ServerUrl) ? null : ServerUrl.Trim();
        _config.Server.AutoConnect = AutoConnect;
        _config.Auth.ClientId = string.IsNullOrWhiteSpace(ClientId) ? null : ClientId;
        _config.Auth.ClientSecret = string.IsNullOrWhiteSpace(ClientSecret) ? null : ClientSecret;
        SaveNote = AutoConnect ? "Uloženo — připojí se automaticky při startu." : "Uloženo.";
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

    partial void OnSelectedPageChanged(PageViewModel? oldValue, PageViewModel? newValue)
    {
        if (oldValue is not null) _ = oldValue.DeactivateAsync();
        if (newValue is not null && IsConnected) _ = newValue.ActivateAsync();
    }
}
