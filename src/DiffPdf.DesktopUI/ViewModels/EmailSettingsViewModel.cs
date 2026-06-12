using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffPdf.Client;
using DiffPdf.DesktopUI.Services;

namespace DiffPdf.DesktopUI.ViewModels;

/// <summary>
/// SMTP server + odesílací účet pro e-mailové notifikace (záložka v Konfiguraci). Heslo se ze serveru nikdy
/// nevrací — jen příznak, že je nastavené; prázdné heslo při uložení ponechá to uložené. Umožní i poslat
/// testovací e-mail pro ověření.
/// </summary>
public partial class EmailSettingsViewModel : ViewModelBase
{
    private readonly ServerSession _session;

    [ObservableProperty] private string _host = string.Empty;
    [ObservableProperty] private int _port = 587;
    [ObservableProperty] private bool _useSsl = true;
    [ObservableProperty] private string _username = string.Empty;
    [ObservableProperty] private string _password = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PasswordPlaceholder))]
    private bool _hasStoredPassword;
    [ObservableProperty] private string _fromAddress = string.Empty;
    [ObservableProperty] private string _fromName = string.Empty;
    [ObservableProperty] private long _version;
    [ObservableProperty] private string? _info;
    [ObservableProperty] private string _testTo = string.Empty;

    /// <summary>Hints that leaving the password blank keeps the one already saved server-side.</summary>
    public string PasswordPlaceholder => HasStoredPassword ? "(beze změny)" : "heslo SMTP účtu";

    public EmailSettingsViewModel(ServerSession session) => _session = session;

    public Task LoadAsync() => RunAsync(async () =>
    {
        var s = await _session.Require().GetEmailSettingsAsync();
        Host = s.Host;
        Port = s.Port;
        UseSsl = s.UseSsl;
        Username = s.Username ?? string.Empty;
        Password = string.Empty;
        HasStoredPassword = s.HasPassword;
        FromAddress = s.FromAddress;
        FromName = s.FromName ?? string.Empty;
        Version = s.Version;
    });

    [RelayCommand]
    private Task SaveAsync() => RunAsync(async () =>
    {
        // Klientská kontrola před voláním serveru — ať uživatel dostane srozumitelnou odpověď hned.
        if (Validate() is { } validation) { Info = null; Error = validation; return; }
        var saved = await _session.Require().UpdateEmailSettingsAsync(new UpdateEmailSettingsRequest
        {
            Host = Host,
            Port = Port,
            UseSsl = UseSsl,
            Username = string.IsNullOrWhiteSpace(Username) ? null : Username,
            Password = string.IsNullOrEmpty(Password) ? null : Password, // blank => keep stored
            FromAddress = FromAddress,
            FromName = string.IsNullOrWhiteSpace(FromName) ? null : FromName,
            Version = Version,
        });
        Version = saved.Version;
        HasStoredPassword = saved.HasPassword;
        Password = string.Empty;
        Info = "Nastavení e-mailu uloženo.";
        ToastSink?.Show(Info, ToastKind.Success);
    });

    [RelayCommand]
    private Task SendTestAsync() => RunAsync(async () =>
    {
        var to = TestTo?.Trim() ?? string.Empty;
        if (to.Length == 0) { Info = "Zadej adresu, na kterou poslat test."; return; }
        if (!to.Contains('@')) { Info = null; Error = $"{UiText.Quote(to)} není e-mailová adresa."; return; }
        await _session.Require().SendTestEmailAsync(new SendTestEmailRequest { To = to });
        Info = $"Testovací e-mail odeslán na {to}.";
        ToastSink?.Show(Info, ToastKind.Success);
    });

    /// <summary>Povinná pole pro funkční odesílání: SMTP server a odesílací adresa (s '@'). Null = OK.</summary>
    private string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Host)) return "Zadej adresu SMTP serveru.";
        if (Port is < 1 or > 65535) return "Port musí být mezi 1 a 65535.";
        var from = FromAddress?.Trim() ?? string.Empty;
        if (from.Length == 0) return "Zadej odesílací adresu (pole „Odesílatel“).";
        if (!from.Contains('@')) return $"{UiText.Quote(from)} není e-mailová adresa.";
        return null;
    }
}
