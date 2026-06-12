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
    private readonly ToastService _toasts;
    private readonly NotificationCenterService _notificationCenter;

    public IReadOnlyList<PageViewModel> Pages { get; }

    /// <summary>Backs the transient toast overlay in the shell window.</summary>
    public ToastService Toasts => _toasts;

    /// <summary>Backs the bell button + event feed flyout in the title bar.</summary>
    public NotificationCenterService NotificationCenter => _notificationCenter;

    [ObservableProperty] private PageViewModel? _selectedPage;
    [ObservableProperty] private string _serverUrl = "http://localhost:5275";
    [ObservableProperty] private string _clientId = string.Empty;
    [ObservableProperty] private string _clientSecret = string.Empty;
    [ObservableProperty] private bool _isConnected;
    // Drives visibility of the ClientId/Secret fields — only shown when the connected server requires auth.
    [ObservableProperty] private bool _authEnabled;
    [ObservableProperty] private string _connectionStatus = "Nepřipojeno";
    // True while the REST session is up but the realtime hub is down/retrying — pushes are being missed,
    // so the shell shows a warning badge ("data se mohou opožďovat") until the hub reconnects.
    [ObservableProperty] private bool _realtimeDegraded;
    // Persist + auto-connect on next start (the connection gear's "remember" toggle).
    [ObservableProperty] private bool _autoConnect = true;
    [ObservableProperty] private string? _saveNote;

    public MainViewModel(ServerSession session, JobProgressHubClient hub, NavigationService navigation,
        ClientConfig config, ServerDiscoveryClient discovery, ClientSettingsStore settings, ToastService toasts,
        NotificationCenterService notificationCenter, IEnumerable<PageViewModel> pages)
    {
        _session = session;
        _hub = hub;
        _config = config;
        _discovery = discovery;
        _settings = settings;
        _toasts = toasts;
        _notificationCenter = notificationCenter;
        Pages = pages.OrderBy(p => p.NavOrder).ToList();
        navigation.Navigated += page => SelectedPage = page;
        // Degraded only while a REST session exists — when fully disconnected the shell overlay says it all.
        hub.RealtimeStateChanged += up => RealtimeDegraded = IsConnected && !up;

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
        RealtimeDegraded = false; // optimistic — the hub start below corrects it within seconds if it fails
        AuthEnabled = _session.ServerRequiresAuth;
        ConnectionStatus = $"Připojeno: {ServerUrl}";
        _toasts.Show($"Připojeno k {ServerUrl}.", ToastKind.Success);

        try { await _hub.EnsureStartedAsync(); } catch { /* live progress is best-effort */ }
        // The event feed (bell): initial history fill + scope-group join for live pushes. Best-effort —
        // a feed hiccup must not fail the connect; the next reconnect replay catches up.
        try { await _notificationCenter.InitializeAsync(); } catch { /* feed is best-effort */ }

        SelectedPage ??= Pages.FirstOrDefault();
        if (SelectedPage is not null)
            await SelectedPage.ActivateAsync();
    }

    [RelayCommand]
    private Task DisconnectAsync() => RunAsync(async () =>
    {
        await _hub.StopAsync();
        _session.Disconnect();
        _notificationCenter.Reset();
        IsConnected = false;
        RealtimeDegraded = false;
        ConnectionStatus = "Nepřipojeno";
        _toasts.Show("Odpojeno od serveru.", ToastKind.Info);
    });

    partial void OnSelectedPageChanged(PageViewModel? oldValue, PageViewModel? newValue)
    {
        if (oldValue is not null) _ = oldValue.DeactivateAsync();
        if (newValue is not null && IsConnected) _ = newValue.ActivateAsync();
    }
}
