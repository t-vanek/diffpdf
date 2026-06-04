using DiffPdf.Client;
using DiffPdf.DesktopUI.Services;

namespace DiffPdf.DesktopUI.ViewModels;

/// <summary>
/// Globální nastavení aplikace — kořen dědičnosti pro spouštěče i porovnávače. Hostuje sdílený editor
/// <see cref="ScopeSettingsViewModel"/> na úrovni Global (bez voliče zdroje); větve a instance dědí z této
/// konfigurace, pokud si nezvolí vlastní. Nahrazuje původní samostatnou stránku „Spouštěče".
/// </summary>
public sealed class SettingsViewModel : PageViewModel
{
    public override string Title => "Konfigurace";
    public override int NavOrder => 100;

    public ScopeSettingsViewModel Editor { get; }

    public SettingsViewModel(ServerSession session) =>
        Editor = new ScopeSettingsViewModel(session, ConfigScopeLevel.Global, branchKey: null, instanceKey: null, isModal: false);

    public override Task ActivateAsync() => Editor.LoadAsync();
}
