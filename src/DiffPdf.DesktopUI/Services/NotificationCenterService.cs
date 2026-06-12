using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffPdf.Client;
using DiffPdf.DesktopUI.ViewModels;

namespace DiffPdf.DesktopUI.Services;

/// <summary>
/// Centrum notifikací: trvalý kanál systémových událostí (výsledky porovnání, běhy automatizací, nedoručené
/// e-maily, zotavení po pádu). Živé eventy chodí SignalR pushem; po reconnectu se zmeškané dotáhnou přes
/// kurzor <c>sinceSeq</c> (replay), takže výpadek spojení žádnou událost neztratí — to je zásadní rozdíl
/// proti toastům, které zmizí. Badge počítá jen nepřečtené Warning/Error (rutinní Info nepípá); „přečteno"
/// (watermark Seq) se persistuje do client-settings, takže přežije restart aplikace.
/// </summary>
public sealed partial class NotificationCenterService : ObservableObject
{
    private const int MaxItems = 200;

    private readonly ServerSession _session;
    private readonly JobProgressHubClient _hub;
    private readonly ClientSettingsStore _settings;
    private readonly ToastService _toasts;

    private long _lastSeenSeq; // persisted "přečteno" watermark
    private long _maxKnownSeq; // replay cursor (the highest Seq ever seen)

    /// <summary>Události, nejnovější první (omezeno na posledních ~200).</summary>
    public ObservableCollection<SystemEventItemViewModel> Events { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnread), nameof(BadgeText))]
    private int _unreadCount;

    [ObservableProperty] private bool _hasNoEvents = true;

    public bool HasUnread => UnreadCount > 0;
    public string BadgeText => UnreadCount > 9 ? "9+" : UnreadCount.ToString();

    public NotificationCenterService(ServerSession session, JobProgressHubClient hub, ClientSettingsStore settings, ToastService toasts)
    {
        _session = session;
        _hub = hub;
        _settings = settings;
        _toasts = toasts;
        hub.SystemEventReceived += OnEvent;            // raised on the UI thread
        hub.Reconnected += () => _ = CatchUpAsync();   // replay everything missed while offline
    }

    /// <summary>Po připojení: join scope skupiny (tam chodí systemEvent push) + počáteční naplnění historie.</summary>
    public async Task InitializeAsync()
    {
        _lastSeenSeq = _settings.LoadLastSeenEventSeq();
        try { await _hub.JoinScopeAsync(); } catch { /* realtime is best-effort; the replay covers gaps */ }

        var recent = await _session.Require().ListSystemEventsAsync(limit: 100); // newest first
        Events.Clear();
        foreach (var e in recent)
            Events.Add(new SystemEventItemViewModel(e));
        HasNoEvents = Events.Count == 0;
        _maxKnownSeq = recent.Count > 0 ? recent.Max(e => e.Seq) : 0;

        // An in-memory server (dev) restarts its Seq counter from 0 — a stale higher watermark would
        // permanently mute the badge. Clamp it down so counting starts fresh.
        if (_lastSeenSeq > _maxKnownSeq)
        {
            _lastSeenSeq = _maxKnownSeq;
            _settings.SaveLastSeenEventSeq(_lastSeenSeq);
        }

        UnreadCount = recent.Count(IsBadgeWorthy);
    }

    /// <summary>Po odpojení: vyprázdnit feed (po novém připojení se naplní znovu).</summary>
    public void Reset()
    {
        Events.Clear();
        HasNoEvents = true;
        UnreadCount = 0;
        _maxKnownSeq = 0;
    }

    private void OnEvent(SystemEvent e)
    {
        if (Events.Any(x => x.Event.Seq == e.Seq))
            return; // push/replay race — every event lands exactly once

        Events.Insert(0, new SystemEventItemViewModel(e));
        while (Events.Count > MaxItems)
            Events.RemoveAt(Events.Count - 1);
        HasNoEvents = false;

        if (e.Seq > _maxKnownSeq) _maxKnownSeq = e.Seq;
        if (IsBadgeWorthy(e)) UnreadCount++;
        // Errors deserve an immediate toast on top of the persistent feed entry.
        if (e.Severity == SystemEventSeverity.Error)
            _toasts.Show(e.Message, ToastKind.Error);
    }

    /// <summary>Replay po reconnectu: dotáhne vše se Seq &gt; poslední známý kurzor (oldest first).</summary>
    private async Task CatchUpAsync()
    {
        if (!_session.IsConnected) return;
        try
        {
            var missed = await _session.Require().ListSystemEventsAsync(sinceSeq: _maxKnownSeq, limit: 500);
            foreach (var e in missed)
                OnEvent(e); // dedups, counts unread, toasts errors
        }
        catch { /* best-effort; the next reconnect (or restart fill) catches up */ }
    }

    [RelayCommand]
    private void MarkAllSeen()
    {
        _lastSeenSeq = _maxKnownSeq;
        UnreadCount = 0;
        _settings.SaveLastSeenEventSeq(_lastSeenSeq);
    }

    /// <summary>Badge počítá jen nepřečtené problémy — rutinní Info (dokončená porovnání, Ok běhy) nepípá.</summary>
    private bool IsBadgeWorthy(SystemEvent e) =>
        e.Seq > _lastSeenSeq && e.Severity is SystemEventSeverity.Warning or SystemEventSeverity.Error;
}
