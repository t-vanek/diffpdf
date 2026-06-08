using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffPdf.Client;
using DiffPdf.DesktopUI.Services;

namespace DiffPdf.DesktopUI.ViewModels;

/// <summary>
/// Reusable editor for a scope's trigger + comparer configuration. Used both inside the per-branch /
/// per-instance settings dialog (modal) and embedded in the global "Nastavení" page. It only lets the user
/// pick a configuration source and edit this level's custom payloads — the inheritance / effective-value
/// computation is done server-side and surfaced here purely as read-only "currently used" information.
/// </summary>
public partial class ScopeSettingsViewModel : ViewModelBase, IModalViewModel
{
    private readonly ServerSession _session;
    private readonly string? _branchKey;
    private readonly string? _instanceKey;
    private long _version;
    private BatchGate? _triggerGate; // preserved across saves (no dedicated editor yet)

    /// <summary>Raised when the (modal) dialog should close — after a successful save or on cancel.</summary>
    public event Action? CloseRequested;

    public ConfigScopeLevel Level { get; }
    public bool IsModal { get; }
    public bool IsGlobal => Level == ConfigScopeLevel.Global;

    /// <summary>Source selectors only apply below the global level (global is the root of every chain).</summary>
    public bool IsSourceSelectable => Level != ConfigScopeLevel.Global;

    public string WindowTitle { get; }
    public string Heading { get; }

    /// <summary>Allowed sources for this level: branch → Global|Custom; instance → Branch|Custom.</summary>
    public ConfigSource[] SourceOptions { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TriggerFieldsVisible))]
    private ConfigSource _triggerSource;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ComparerFieldsVisible))]
    private ConfigSource _comparerSource;

    [ObservableProperty] private string _searchPattern = "*.pdf";
    [ObservableProperty] private bool _recursive = true;
    [ObservableProperty] private decimal _maxDegreeOfParallelism;

    /// <summary>The comparer (ComparisonOptions) editor, reused from the single-compare screen.</summary>
    public ComparisonOptionsViewModel Comparer { get; } = new();

    /// <summary>Server-resolved "currently used" source descriptions (no inheritance computed in the UI).</summary>
    [ObservableProperty] private string _triggerSourceInfo = string.Empty;
    [ObservableProperty] private string _comparerSourceInfo = string.Empty;
    [ObservableProperty] private string? _info;

    /// <summary>Scheduled run: a cron (UTC) that enqueues this scope's instances. Only below the global level.</summary>
    [ObservableProperty] private bool _scheduleEnabled;
    [ObservableProperty] private string _scheduleCron = "0 6 * * *";

    /// <summary>The schedule section applies to a branch or instance, not the global root.</summary>
    public bool IsScheduleAvailable => !IsGlobal;

    /// <summary>Trigger fields are shown only when this level supplies a custom payload (global is always custom).</summary>
    public bool TriggerFieldsVisible => IsGlobal || TriggerSource == ConfigSource.Custom;
    public bool ComparerFieldsVisible => IsGlobal || ComparerSource == ConfigSource.Custom;

    public ScopeSettingsViewModel(ServerSession session, ConfigScopeLevel level, string? branchKey, string? instanceKey, bool isModal)
    {
        _session = session;
        Level = level;
        _branchKey = branchKey;
        _instanceKey = instanceKey;
        IsModal = isModal;

        SourceOptions = level switch
        {
            ConfigScopeLevel.Branch => [ConfigSource.Global, ConfigSource.Custom],
            ConfigScopeLevel.Instance => [ConfigSource.Branch, ConfigSource.Custom],
            _ => [],
        };

        (WindowTitle, Heading) = level switch
        {
            ConfigScopeLevel.Branch => ($"Konfigurace větve: {branchKey}", $"Konfigurace větve „{branchKey}“"),
            ConfigScopeLevel.Instance => ($"Konfigurace instance: {instanceKey}", $"Konfigurace instance „{instanceKey}“"),
            _ => ("Globální konfigurace", "Globální konfigurace aplikace"),
        };
    }

    public async Task LoadAsync() => await RunAsync(async () =>
    {
        var client = _session.Require();
        var cfg = Level switch
        {
            ConfigScopeLevel.Branch => await client.GetBranchConfigAsync(_branchKey!),
            ConfigScopeLevel.Instance => await client.GetInstanceConfigAsync(_branchKey!, _instanceKey!),
            _ => await client.GetGlobalConfigAsync(),
        };
        if (cfg is null)
        {
            Error = "Konfiguraci se nepodařilo načíst (scope neexistuje).";
            return;
        }
        Apply(cfg);

        if (IsScheduleAvailable)
        {
            var sched = Level == ConfigScopeLevel.Branch
                ? await client.GetBranchScheduleAsync(_branchKey!)
                : await client.GetInstanceScheduleAsync(_branchKey!, _instanceKey!);
            if (sched is not null)
            {
                ScheduleEnabled = sched.Enabled;
                if (!string.IsNullOrWhiteSpace(sched.Cron))
                    ScheduleCron = sched.Cron!;
            }
        }
    });

    private void Apply(ScopeConfigResponse cfg)
    {
        _version = cfg.Version;
        TriggerSource = cfg.TriggerSource;
        ComparerSource = cfg.ComparerSource;

        SearchPattern = cfg.TriggerConfig.SearchPattern;
        Recursive = cfg.TriggerConfig.Recursive;
        MaxDegreeOfParallelism = cfg.TriggerConfig.MaxDegreeOfParallelism;
        _triggerGate = cfg.TriggerConfig.Gate;

        Comparer.Load(cfg.ComparisonOptions);

        TriggerSourceInfo = $"Aktuálně se používá: {Describe(cfg.TriggerResolvedFrom)}";
        ComparerSourceInfo = $"Aktuálně se používá: {Describe(cfg.ComparerResolvedFrom)}";
    }

    [RelayCommand]
    private Task SaveAsync() => RunAsync(async () =>
    {
        // Validate the cron client-side first so a bad value never leaves a half-saved config behind.
        if (IsScheduleAvailable && ScheduleEnabled && !LooksLikeCron(ScheduleCron))
        {
            Error = "Neplatný plán: zadejte 5 polí oddělených mezerou (např. „0 6 * * *“).";
            return;
        }

        var client = _session.Require();
        var req = new UpsertScopeConfigRequest
        {
            TriggerSource = IsGlobal ? ConfigSource.Custom : TriggerSource,
            TriggerConfig = new TriggerConfig
            {
                SearchPattern = string.IsNullOrWhiteSpace(SearchPattern) ? "*.pdf" : SearchPattern.Trim(),
                Recursive = Recursive,
                MaxDegreeOfParallelism = (int)MaxDegreeOfParallelism,
                Gate = _triggerGate,
            },
            ComparerSource = IsGlobal ? ConfigSource.Custom : ComparerSource,
            ComparisonOptions = Comparer.ToOptions(),
            Version = _version,
        };

        var saved = Level switch
        {
            ConfigScopeLevel.Branch => await client.UpdateBranchConfigAsync(_branchKey!, req),
            ConfigScopeLevel.Instance => await client.UpdateInstanceConfigAsync(_branchKey!, _instanceKey!, req),
            _ => await client.UpdateGlobalConfigAsync(req),
        };

        Apply(saved);

        if (IsScheduleAvailable)
        {
            var schedReq = new SetScheduleRequest { Enabled = ScheduleEnabled, Cron = ScheduleCron.Trim() };
            _ = Level == ConfigScopeLevel.Branch
                ? await client.SetBranchScheduleAsync(_branchKey!, schedReq)
                : await client.SetInstanceScheduleAsync(_branchKey!, _instanceKey!, schedReq);
        }

        Info = "Uloženo.";
        if (IsModal)
            CloseRequested?.Invoke();
    });

    /// <summary>A loose 5-field check; the server does the full cron validation.</summary>
    private static bool LooksLikeCron(string cron) =>
        cron.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length == 5;

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke();

    private static string Describe(ConfigScopeLevel level) => level switch
    {
        ConfigScopeLevel.Global => "globální konfigurace",
        ConfigScopeLevel.Branch => "konfigurace větve",
        ConfigScopeLevel.Instance => "vlastní konfigurace instance",
        _ => level.ToString(),
    };
}
