using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffPdf.Client;
using DiffPdf.DesktopUI.Services;

namespace DiffPdf.DesktopUI.ViewModels;

/// <summary>Notification subscriptions: list + CRUD (webhook/e-mail, events, optional scope).</summary>
public partial class SubscriptionsViewModel : PageViewModel
{
    private readonly ServerSession _session;
    private readonly DialogService _dialogs;

    public override string Title => "Notifikace";
    public override string Icon => "✉︎";
    public override int NavOrder => 7;

    public string[] Channels { get; } = ["webhook", "email"];
    public ObservableCollection<SubscriptionResponse> Subscriptions { get; } = [];

    [ObservableProperty] private SubscriptionResponse? _selected;
    [ObservableProperty] private string _channel = "webhook";
    [ObservableProperty] private string _target = string.Empty;
    [ObservableProperty] private bool _eventCompleted = true;
    [ObservableProperty] private bool _eventGateViolated = true;
    [ObservableProperty] private bool _eventFailed;
    [ObservableProperty] private string _branchKey = string.Empty;
    [ObservableProperty] private string _instanceKey = string.Empty;
    [ObservableProperty] private bool _enabled = true;
    [ObservableProperty] private long? _editingVersion;
    [ObservableProperty] private string? _info;
    [ObservableProperty] private bool _hasNoSubscriptions;

    public bool IsEditing => EditingVersion is not null;

    public SubscriptionsViewModel(ServerSession session, DialogService dialogs)
    {
        _session = session;
        _dialogs = dialogs;
    }

    public override Task ActivateAsync() => RunAsync(LoadAsync);

    private async Task LoadAsync()
    {
        Subscriptions.Clear();
        foreach (var s in await _session.Require().ListSubscriptionsAsync()) Subscriptions.Add(s);
        HasNoSubscriptions = Subscriptions.Count == 0;
    }

    partial void OnEditingVersionChanged(long? value) => OnPropertyChanged(nameof(IsEditing));

    [RelayCommand]
    private Task RefreshAsync() => RunAsync(LoadAsync);

    [RelayCommand]
    private void New()
    {
        EditingVersion = null;
        Channel = "webhook";
        Target = string.Empty;
        EventCompleted = EventGateViolated = true;
        EventFailed = false;
        BranchKey = InstanceKey = string.Empty;
        Enabled = true;
        Info = null;
    }

    [RelayCommand]
    private void EditSelected()
    {
        if (Selected is not { } s) return;
        EditingVersion = s.Version;
        Channel = s.Channel;
        Target = s.Target;
        EventCompleted = s.Events.Contains(NotificationEvent.Completed);
        EventGateViolated = s.Events.Contains(NotificationEvent.GateViolated);
        EventFailed = s.Events.Contains(NotificationEvent.Failed);
        BranchKey = s.BranchKey ?? string.Empty;
        InstanceKey = s.InstanceKey ?? string.Empty;
        Enabled = s.Enabled;
        Info = $"Editace odběru {s.Id} (v{s.Version}).";
    }

    [RelayCommand]
    private Task SaveAsync() => RunAsync(async () =>
    {
        var client = _session.Require();
        string? bk = string.IsNullOrWhiteSpace(BranchKey) ? null : BranchKey;
        string? ik = string.IsNullOrWhiteSpace(InstanceKey) ? null : InstanceKey;

        if (EditingVersion is { } version && Selected is { } sel)
        {
            await client.UpdateSubscriptionAsync(sel.Id, new UpdateSubscriptionRequest
            {
                Channel = Channel, Target = Target, Events = Events(),
                BranchKey = bk, InstanceKey = ik, Enabled = Enabled, Version = version,
            });
            Info = "Uloženo (úprava).";
        }
        else
        {
            await client.CreateSubscriptionAsync(new CreateSubscriptionRequest
            {
                Channel = Channel, Target = Target, Events = Events(),
                BranchKey = bk, InstanceKey = ik, Enabled = Enabled,
            });
            Info = "Vytvořeno.";
        }
        await LoadAsync();
        _dialogs.ShowToast(Info!, ToastKind.Success);
    });

    [RelayCommand]
    private Task DeleteAsync() => RunAsync(async () =>
    {
        if (Selected is not { } s) throw new InvalidOperationException("Vyber odběr.");
        if (!await _dialogs.ConfirmAsync("Smazat odběr", $"Opravdu smazat odběr {s.Channel} → {s.Target}?"))
            return;
        await _session.Require().DeleteSubscriptionAsync(s.Id);
        Info = "Smazáno.";
        await LoadAsync();
        _dialogs.ShowToast("Odběr smazán.", ToastKind.Success);
    });

    private IReadOnlyList<NotificationEvent> Events()
    {
        var list = new List<NotificationEvent>();
        if (EventCompleted) list.Add(NotificationEvent.Completed);
        if (EventGateViolated) list.Add(NotificationEvent.GateViolated);
        if (EventFailed) list.Add(NotificationEvent.Failed);
        return list;
    }
}
