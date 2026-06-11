using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.Mail;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffPdf.Client;
using DiffPdf.DesktopUI.Services;

namespace DiffPdf.DesktopUI.ViewModels;

/// <summary>A branch/instance key with a checkbox — backs the scope multi-selects in the rule editor.</summary>
public partial class CheckableKey(string key) : ObservableObject
{
    public string Key { get; } = key;
    [ObservableProperty] private bool _isChecked;
}

/// <summary>One e-mail rule as shown in the master grid (display summaries over the underlying response).</summary>
public sealed class NotificationRuleRow(SubscriptionResponse model)
{
    public SubscriptionResponse Model => model;

    public string DisplayName => string.IsNullOrWhiteSpace(model.Name) ? "(bez názvu)" : model.Name;

    public string RecipientsSummary => model.Recipients.Count switch
    {
        0 => "—",
        1 => model.Recipients[0],
        2 => string.Join(", ", model.Recipients),
        _ => $"{model.Recipients[0]} +{model.Recipients.Count - 1}",
    };

    public string ScopeSummary
    {
        get
        {
            string b = model.BranchKeys.Count == 0 ? "všechny větve" : string.Join(", ", model.BranchKeys);
            string i = model.InstanceKeys.Count == 0 ? "všechny instance" : string.Join(", ", model.InstanceKeys);
            return $"{b} · {i}";
        }
    }

    public string EventsSummary => $"{model.Events.Count} událostí";
    public bool Enabled => model.Enabled;
}

/// <summary>
/// Notifikace: seznam e-mailových pravidel + editor (název, příjemci, výběr větví/instancí, události, zap/vyp).
/// Každé pravidlo pošle e-mail svým příjemcům, když nastane některá ze zvolených událostí v rámci jeho rozsahu
/// (prázdný výběr větví/instancí = jakákoliv). SMTP server se nastavuje v Konfiguraci → E-mail.
/// </summary>
public partial class SubscriptionsViewModel : PageViewModel
{
    private readonly ServerSession _session;
    private readonly DialogService _dialogs;

    // The rule currently loaded in the editor. Tracked separately from the grid's Selected so a background reload
    // (nav re-click / F5) that clears the grid selection never silently flips Save from update → create.
    private Guid? _editingId;

    public override string Title => "Notifikace";
    public override string Icon => "✉︎";
    public override int NavOrder => 7;

    public ObservableCollection<NotificationRuleRow> Rules { get; } = [];
    [ObservableProperty] private NotificationRuleRow? _selected;
    [ObservableProperty] private bool _hasNoRules;
    [ObservableProperty] private string? _info;

    // ---- Editor ----
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EditorHeading))]
    private long? _editingVersion; // null = creating a new rule
    public bool IsEditing => EditingVersion is not null;
    public string EditorHeading => IsEditing ? "Úprava pravidla" : "Nové pravidlo";

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private bool _enabled = true;

    public ObservableCollection<string> Recipients { get; } = [];
    [ObservableProperty] private string _newRecipient = string.Empty;

    public ObservableCollection<CheckableKey> BranchChoices { get; } = [];
    public ObservableCollection<CheckableKey> InstanceChoices { get; } = [];

    /// <summary>Summaries shown on the scope dropdown buttons (empty selection = any).</summary>
    public string BranchScopeSummary => Summarize(BranchChoices, "Všechny větve");
    public string InstanceScopeSummary => Summarize(InstanceChoices, "Všechny instance");

    /// <summary>True when the rule restricts the scope (anything ticked) — drives the dropdowns' accent look.</summary>
    public bool HasBranchSelection => BranchChoices.Any(c => c.IsChecked);
    public bool HasInstanceSelection => InstanceChoices.Any(c => c.IsChecked);

    private static string Summarize(IEnumerable<CheckableKey> choices, string allLabel)
    {
        var picked = choices.Where(c => c.IsChecked).Select(c => c.Key).ToList();
        return picked.Count switch
        {
            0 => allLabel,
            1 or 2 => string.Join(", ", picked),
            _ => $"Vybráno: {picked.Count}",
        };
    }

    // Events — Úlohy
    [ObservableProperty] private bool _evtCompleted = true;
    [ObservableProperty] private bool _evtCompletedWithErrors = true;
    [ObservableProperty] private bool _evtFailed;
    [ObservableProperty] private bool _evtJobStalled;
    // Events — Server a automatizace
    [ObservableProperty] private bool _evtHealthDegraded;
    [ObservableProperty] private bool _evtReadinessFailed;
    [ObservableProperty] private bool _evtStructureDrift;
    [ObservableProperty] private bool _evtAutomationRecovered;
    [ObservableProperty] private bool _evtAutomationFailing;

    public SubscriptionsViewModel(ServerSession session, DialogService dialogs)
    {
        _session = session;
        _dialogs = dialogs;
    }

    public override Task ActivateAsync() => RunAsync(LoadAsync);
    public override Task ReloadAsync() => RunAsync(LoadAsync);

    partial void OnEditingVersionChanged(long? value) => OnPropertyChanged(nameof(IsEditing));

    private async Task LoadAsync()
    {
        await LoadScopeChoicesAsync();
        var rules = await _session.Require().ListSubscriptionsAsync();
        Rules.Clear();
        foreach (var r in rules) Rules.Add(new NotificationRuleRow(r));
        HasNoRules = Rules.Count == 0;
    }

    /// <summary>Instance keys per branch — feeds the branch-driven narrowing of the instance offer.</summary>
    private readonly Dictionary<string, SortedSet<string>> _instancesByBranch = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Loads the branch + (distinct) instance keys that back the scope multi-selects, preserving ticks.</summary>
    private async Task LoadScopeChoicesAsync()
    {
        var client = _session.Require();
        var branches = await client.ListBranchesAsync();

        var checkedBranches = BranchChoices.Where(c => c.IsChecked).Select(c => c.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        BranchChoices.Clear();
        foreach (var b in branches)
            BranchChoices.Add(Track(new CheckableKey(b.Key) { IsChecked = checkedBranches.Contains(b.Key) }, OnBranchCheckChanged));

        // Instance keys are matched flat (key-only) on the server; remember them per branch so the
        // instance dropdown can narrow its offer to the selected branches.
        _instancesByBranch.Clear();
        foreach (var b in branches)
        {
            var keys = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var inst in await client.ListInstancesAsync(b.Key))
                keys.Add(inst.Key);
            _instancesByBranch[b.Key] = keys;
        }

        RebuildInstanceChoices();
        OnPropertyChanged(nameof(BranchScopeSummary));
        OnPropertyChanged(nameof(HasBranchSelection));
    }

    /// <summary>
    /// The instance offer follows the branch selection: with branches restricted, only their instances are
    /// listed. Any already-ticked key stays listed (and ticked) even when its branch is filtered out, so
    /// narrowing branches never silently drops part of a saved rule.
    /// </summary>
    private void RebuildInstanceChoices()
    {
        var pickedBranches = BranchChoices.Where(c => c.IsChecked).Select(c => c.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var visible = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (branch, keys) in _instancesByBranch)
        {
            if (pickedBranches.Count == 0 || pickedBranches.Contains(branch))
                visible.UnionWith(keys);
        }

        var ticked = InstanceChoices.Where(c => c.IsChecked).Select(c => c.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        visible.UnionWith(ticked);

        InstanceChoices.Clear();
        foreach (var k in visible)
            InstanceChoices.Add(Track(new CheckableKey(k) { IsChecked = ticked.Contains(k) }, OnInstanceCheckChanged));

        OnPropertyChanged(nameof(InstanceScopeSummary));
        OnPropertyChanged(nameof(HasInstanceSelection));
    }

    /// <summary>A saved rule may reference instance keys outside the current offer (deleted instance, branch
    /// filtered out) — list them anyway so editing the rule shows, and can untick, its full selection.</summary>
    private void EnsureInstanceChoices(IReadOnlyList<string> keys)
    {
        foreach (var k in keys)
        {
            if (!InstanceChoices.Any(c => string.Equals(c.Key, k, StringComparison.OrdinalIgnoreCase)))
                InstanceChoices.Add(Track(new CheckableKey(k), OnInstanceCheckChanged));
        }
    }

    // Live-update the dropdown summaries as the user ticks scope checkboxes.
    private static CheckableKey Track(CheckableKey key, PropertyChangedEventHandler handler)
    {
        key.PropertyChanged += handler;
        return key;
    }

    private void OnBranchCheckChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(CheckableKey.IsChecked)) return;
        OnPropertyChanged(nameof(BranchScopeSummary));
        OnPropertyChanged(nameof(HasBranchSelection));
        RebuildInstanceChoices(); // the instance offer follows the branch selection
    }

    private void OnInstanceCheckChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(CheckableKey.IsChecked)) return;
        OnPropertyChanged(nameof(InstanceScopeSummary));
        OnPropertyChanged(nameof(HasInstanceSelection));
    }

    // Selecting a rule loads it into the editor (edit mode); clearing the selection leaves the editor as-is.
    partial void OnSelectedChanged(NotificationRuleRow? value)
    {
        if (value is null) return;
        LoadIntoEditor(value.Model);
    }

    private void LoadIntoEditor(SubscriptionResponse s)
    {
        _editingId = s.Id;
        EditingVersion = s.Version;
        Name = s.Name;
        Enabled = s.Enabled;

        Recipients.Clear();
        foreach (var r in s.Recipients) Recipients.Add(r);

        SetChecks(BranchChoices, s.BranchKeys);
        EnsureInstanceChoices(s.InstanceKeys);
        SetChecks(InstanceChoices, s.InstanceKeys);
        ApplyEvents(s.Events);
        Info = $"Úprava pravidla: {(string.IsNullOrWhiteSpace(s.Name) ? s.Id.ToString() : s.Name)}";
    }

    private static void SetChecks(IEnumerable<CheckableKey> choices, IReadOnlyList<string> selected)
    {
        var set = selected.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var c in choices) c.IsChecked = set.Contains(c.Key);
    }

    [RelayCommand]
    private void New()
    {
        Selected = null;
        _editingId = null;
        EditingVersion = null;
        Name = string.Empty;
        Enabled = true;
        Recipients.Clear();
        NewRecipient = string.Empty;
        foreach (var c in BranchChoices) c.IsChecked = false;
        foreach (var c in InstanceChoices) c.IsChecked = false;
        EvtCompleted = EvtCompletedWithErrors = true;
        EvtFailed = EvtJobStalled = false;
        EvtHealthDegraded = EvtReadinessFailed = EvtStructureDrift = false;
        EvtAutomationRecovered = EvtAutomationFailing = false;
        Info = "Nové pravidlo — vyplň příjemce a události.";
    }

    [RelayCommand]
    private void AddRecipient()
    {
        var email = NewRecipient?.Trim() ?? string.Empty;
        if (email.Length == 0) return;
        if (!MailAddress.TryCreate(email, out _)) { Info = $"Neplatná e-mailová adresa: {email}"; return; }
        if (!Recipients.Any(r => string.Equals(r, email, StringComparison.OrdinalIgnoreCase)))
            Recipients.Add(email);
        NewRecipient = string.Empty;
    }

    [RelayCommand]
    private void RemoveRecipient(string? email)
    {
        if (email is null) return;
        var match = Recipients.FirstOrDefault(r => string.Equals(r, email, StringComparison.OrdinalIgnoreCase));
        if (match is not null) Recipients.Remove(match);
    }

    [RelayCommand]
    private Task SaveAsync() => RunAsync(async () =>
    {
        // Fold a half-typed recipient still sitting in the box into the list before saving.
        if (!string.IsNullOrWhiteSpace(NewRecipient)) AddRecipient();

        if (Recipients.Count == 0) { Info = "Přidej aspoň jednoho příjemce."; return; }
        var events = CollectEvents();
        if (events.Count == 0) { Info = "Vyber aspoň jednu událost."; return; }

        var client = _session.Require();
        var branchKeys = BranchChoices.Where(c => c.IsChecked).Select(c => c.Key).ToList();
        var instanceKeys = InstanceChoices.Where(c => c.IsChecked).Select(c => c.Key).ToList();
        var recipients = Recipients.ToList();

        if (EditingVersion is { } version && _editingId is { } id)
        {
            await client.UpdateSubscriptionAsync(id, new UpdateSubscriptionRequest
            {
                Name = Name, Recipients = recipients, Events = events,
                BranchKeys = branchKeys, InstanceKeys = instanceKeys, Enabled = Enabled, Version = version,
            });
            Info = "Pravidlo uloženo.";
        }
        else
        {
            await client.CreateSubscriptionAsync(new CreateSubscriptionRequest
            {
                Name = Name, Recipients = recipients, Events = events,
                BranchKeys = branchKeys, InstanceKeys = instanceKeys, Enabled = Enabled,
            });
            Info = "Pravidlo vytvořeno.";
        }
        await LoadAsync();
        _dialogs.ShowToast(Info!, ToastKind.Success);
    });

    [RelayCommand]
    private Task DeleteAsync() => RunAsync(async () =>
    {
        if (Selected is not { } s) { Info = "Vyber pravidlo."; return; }
        if (!await _dialogs.ConfirmAsync("Smazat pravidlo", $"Opravdu smazat pravidlo: {s.DisplayName}?"))
            return;
        await _session.Require().DeleteSubscriptionAsync(s.Model.Id);
        Info = "Pravidlo smazáno.";
        Selected = null;
        New();
        await LoadAsync();
        _dialogs.ShowToast("Pravidlo smazáno.", ToastKind.Success);
    });

    private IReadOnlyList<NotificationEvent> CollectEvents()
    {
        var list = new List<NotificationEvent>();
        if (EvtCompleted) list.Add(NotificationEvent.Completed);
        if (EvtCompletedWithErrors) list.Add(NotificationEvent.CompletedWithErrors);
        if (EvtFailed) list.Add(NotificationEvent.Failed);
        if (EvtJobStalled) list.Add(NotificationEvent.JobStalled);
        if (EvtHealthDegraded) list.Add(NotificationEvent.HealthDegraded);
        if (EvtReadinessFailed) list.Add(NotificationEvent.ReadinessFailed);
        if (EvtStructureDrift) list.Add(NotificationEvent.StructureDrift);
        if (EvtAutomationRecovered) list.Add(NotificationEvent.AutomationRecovered);
        if (EvtAutomationFailing) list.Add(NotificationEvent.AutomationFailing);
        return list;
    }

    private void ApplyEvents(IReadOnlyList<NotificationEvent> events)
    {
        EvtCompleted = events.Contains(NotificationEvent.Completed);
        EvtCompletedWithErrors = events.Contains(NotificationEvent.CompletedWithErrors);
        EvtFailed = events.Contains(NotificationEvent.Failed);
        EvtJobStalled = events.Contains(NotificationEvent.JobStalled);
        EvtHealthDegraded = events.Contains(NotificationEvent.HealthDegraded);
        EvtReadinessFailed = events.Contains(NotificationEvent.ReadinessFailed);
        EvtStructureDrift = events.Contains(NotificationEvent.StructureDrift);
        EvtAutomationRecovered = events.Contains(NotificationEvent.AutomationRecovered);
        EvtAutomationFailing = events.Contains(NotificationEvent.AutomationFailing);
    }
}
