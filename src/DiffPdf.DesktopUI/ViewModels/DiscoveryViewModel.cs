using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffPdf.Client;
using DiffPdf.DesktopUI.Services;

namespace DiffPdf.DesktopUI.ViewModels;

/// <summary>Discovery: configured network shares and credential-profile names (read-only).</summary>
public partial class DiscoveryViewModel : PageViewModel
{
    private readonly ServerSession _session;
    private readonly DialogService _dialogs;

    public override string Title => "Discovery";
    public override int NavOrder => 8;

    public ObservableCollection<ShareInfo> Shares { get; } = [];
    public ObservableCollection<string> CredentialProfiles { get; } = [];

    [ObservableProperty] private string? _syncInfo;

    public DiscoveryViewModel(ServerSession session, DialogService dialogs)
    {
        _session = session;
        _dialogs = dialogs;
    }

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

    /// <summary>Dry-run scope sync: report what the on-disk &lt;root&gt;/&lt;branch&gt;/&lt;instance&gt; tree would change.</summary>
    [RelayCommand]
    private Task PreviewSyncAsync() => RunAsync(async () =>
        SyncInfo = Summarize(await _session.Require().SyncScopeAsync(apply: false)));

    /// <summary>Apply scope sync: register discovered folders and create the missing skeleton.</summary>
    [RelayCommand]
    private Task ApplySyncAsync() => RunAsync(async () =>
    {
        if (!await _dialogs.ConfirmAsync("Synchronizovat scope",
                "Zaregistruje nalezené složky do databáze a vytvoří chybějící old/new/reports na disku. Pokračovat?"))
            return;
        SyncInfo = Summarize(await _session.Require().SyncScopeAsync(apply: true));
    });

    private static string Summarize(ScopeSyncReport r)
    {
        if (!r.Reachable)
            return $"Kořen nedostupný: {r.Error}";
        int branches = r.Branches.Count(b => b.State == BranchSyncState.Registered);
        int instances = r.Branches.Sum(b => b.Instances.Count(i => i.State == InstanceSyncState.Registered));
        string verb = r.Applied ? "zaregistrováno" : "k registraci";
        return $"{r.Root}: {branches} branch(í), {instances} instancí {verb}; "
             + $"{r.MissingFolders.Count} chybějících složek, {r.OutOfRoot.Count} mimo kořen.";
    }
}
