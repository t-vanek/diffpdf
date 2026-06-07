using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using DiffPdf.Client;
using DiffPdf.DesktopUI.Services;

namespace DiffPdf.DesktopUI.ViewModels;

/// <summary>Discovery: configured network shares and credential-profile names (read-only). Scope synchronization
/// lives on the Větve page (it registers branches/instances from the folder tree).</summary>
public partial class DiscoveryViewModel : PageViewModel
{
    private readonly ServerSession _session;

    public override string Title => "Sdílené složky";
    public override string Icon => "⊞";
    public override int NavOrder => 8;

    public ObservableCollection<ShareInfo> Shares { get; } = [];
    public ObservableCollection<string> CredentialProfiles { get; } = [];

    public DiscoveryViewModel(ServerSession session) => _session = session;

    public override Task ActivateAsync() => RunAsync(LoadAsync);

    private async Task LoadAsync()
    {
        var cfg = await _session.Require().ListSharesAsync();
        Shares.Clear();
        foreach (var s in cfg.Shares) Shares.Add(s);
        CredentialProfiles.Clear();
        foreach (var p in cfg.CredentialProfiles) CredentialProfiles.Add(p);
    }

    [RelayCommand]
    private Task RefreshAsync() => RunAsync(LoadAsync);
}
