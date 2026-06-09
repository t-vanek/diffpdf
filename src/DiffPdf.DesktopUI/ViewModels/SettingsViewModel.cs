using DiffPdf.Client;
using DiffPdf.DesktopUI.Services;

namespace DiffPdf.DesktopUI.ViewModels;

/// <summary>
/// Globální nastavení aplikace ve dvou záložkách: „Spouštěče a porovnávače" (kořen dědičnosti — sdílený editor
/// <see cref="ScopeSettingsViewModel"/> na úrovni Global; větve a instance dědí, pokud si nezvolí vlastní) a
/// „E-mail (SMTP)" (<see cref="EmailSettingsViewModel"/> — server a odesílací účet pro notifikace).
/// </summary>
public sealed class SettingsViewModel : PageViewModel
{
    public override string Title => "Konfigurace";
    public override string Icon => "⚙︎";
    public override int NavOrder => 100;

    public ScopeSettingsViewModel Editor { get; }
    public EmailSettingsViewModel Email { get; }

    public SettingsViewModel(ServerSession session)
    {
        Editor = new ScopeSettingsViewModel(session, ConfigScopeLevel.Global, branchKey: null, instanceKey: null, isModal: false);
        Email = new EmailSettingsViewModel(session);
    }

    public override async Task ActivateAsync()
    {
        await Editor.LoadAsync();
        await Email.LoadAsync();
    }
}
