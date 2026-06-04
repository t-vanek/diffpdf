using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DiffPdf.DesktopUI.ViewModels;

/// <summary>Content view-model hosted inside the Automatizace section; activated when its type is selected.</summary>
public interface IAutomationContent
{
    Task ActivateAsync();
}

/// <summary>One kind of automation in the Automatizace section. <see cref="Content"/> manages that type and
/// is rendered by the ViewLocator (VM → View). New automation types are added by extending the list.</summary>
public sealed record AutomationType(string Title, string Description, IAutomationContent Content);

/// <summary>
/// Automatizace — samostatná sekce pro automatizované akce. Zastřešuje jednotlivé typy automatizací (levý
/// seznam); dnes obsahuje pouze „Automatizované kontroly", ale architektura počítá s přidáním dalších typů.
/// </summary>
public partial class AutomationsViewModel : PageViewModel
{
    public override string Title => "Automatizace";
    public override int NavOrder => 3;

    public IReadOnlyList<AutomationType> Types { get; }

    [ObservableProperty] private AutomationType? _selectedType;

    public AutomationsViewModel(ControlChecksViewModel controlChecks)
    {
        Types =
        [
            new AutomationType(
                "Automatizované kontroly",
                "Plánované kontroly připravenosti, zdraví serveru, struktury a retence.",
                controlChecks),
        ];
        _selectedType = Types.FirstOrDefault();
    }

    public override Task ActivateAsync() => SelectedType?.Content.ActivateAsync() ?? Task.CompletedTask;

    partial void OnSelectedTypeChanged(AutomationType? value) => _ = (value?.Content.ActivateAsync() ?? Task.CompletedTask);
}
