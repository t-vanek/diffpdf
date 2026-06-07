using Avalonia.Threading;
using DiffPdf.Client;
using Microsoft.AspNetCore.SignalR.Client;

namespace DiffPdf.DesktopUI.Services;

/// <summary>
/// Wraps a SignalR <see cref="HubConnection"/> to the server's <c>/hubs/jobs</c> hub. Joins job/branch/
/// instance/trigger groups and raises <see cref="ProgressReceived"/> / <see cref="TriggerEventReceived"/> /
/// <see cref="QueueStateReceived"/> (on the UI thread) for each push. Connects anonymously when auth is off,
/// otherwise with a bearer token from <see cref="TokenSource"/>. Group memberships are tracked and re-joined
/// after an automatic reconnect (a reconnect gets a new connection id, so server-side groups are otherwise lost).
/// </summary>
public sealed class JobProgressHubClient(ServerSession session, TokenSource tokens)
{
    private HubConnection? _connection;

    // Tracked memberships, re-applied on reconnect.
    private readonly object _gate = new();
    private readonly HashSet<Guid> _jobs = [];
    private readonly HashSet<string> _branches = new(StringComparer.Ordinal);
    private readonly HashSet<(string Branch, string Instance)> _instances = [];
    private readonly HashSet<Guid> _triggers = [];
    private bool _scope;

    public event Action<JobProgress>? ProgressReceived;

    /// <summary>Raised (on the UI thread) for each trigger / batch / comparison lifecycle event.</summary>
    public event Action<TriggerEvent>? TriggerEventReceived;

    /// <summary>Raised (on the UI thread) for each per-branch run-queue state push.</summary>
    public event Action<BranchQueueState>? QueueStateReceived;

    /// <summary>Raised (on the UI thread) after an automatic reconnect re-joins groups — listeners should reload to catch anything missed while disconnected.</summary>
    public event Action? Reconnected;

    public bool IsConnected => _connection is { State: HubConnectionState.Connected };

    public Task EnsureStartedAsync()
    {
        if (_connection is not null || session.BaseUrl is null)
            return Task.CompletedTask;

        var url = session.BaseUrl.TrimEnd('/') + "/hubs/jobs";
        var conn = new HubConnectionBuilder()
            .WithUrl(url, o => o.AccessTokenProvider = () => tokens.GetTokenAsync())
            // Reconnect forever (the built-in policy gives up after ~30s). With the desktop driven by realtime
            // push rather than polling, the connection must keep retrying so it self-heals after any outage.
            .WithAutomaticReconnect(new ForeverRetryPolicy())
            .Build();

        conn.On<JobProgress>("jobProgress", p =>
            Dispatcher.UIThread.Post(() => ProgressReceived?.Invoke(p)));

        conn.On<TriggerEvent>("triggerEvent", e =>
            Dispatcher.UIThread.Post(() => TriggerEventReceived?.Invoke(e)));

        conn.On<BranchQueueState>("queueState", s =>
            Dispatcher.UIThread.Post(() => QueueStateReceived?.Invoke(s)));

        // After an automatic reconnect the server-side groups are gone (new connection id) — re-join everything
        // we track, then tell listeners to reload (they may have missed events while the connection was down).
        conn.Reconnected += async _ =>
        {
            await RejoinAllAsync();
            Dispatcher.UIThread.Post(() => Reconnected?.Invoke());
        };

        _connection = conn;
        // Connect in the background and KEEP RETRYING the initial connect. WithAutomaticReconnect only revives an
        // already-established connection; a failed first StartAsync would otherwise leave realtime dead forever
        // (the early-return guard above would never re-attempt). Non-blocking so the page loads over HTTP meanwhile.
        _ = StartWithRetryAsync(conn);
        return Task.CompletedTask;
    }

    private async Task StartWithRetryAsync(HubConnection conn)
    {
        for (int attempt = 0; _connection == conn; attempt++)
        {
            try
            {
                await conn.StartAsync();
            }
            catch
            {
                if (_connection != conn) return; // replaced / stopped meanwhile
                await Task.Delay(TimeSpan.FromSeconds(attempt < 5 ? 2 : 15));
                continue;
            }
            await RejoinAllAsync(); // apply the group memberships requested before we were connected
            if (attempt > 0) Dispatcher.UIThread.Post(() => Reconnected?.Invoke()); // recovered → listeners resync
            return;
        }
    }

    public Task JoinJobAsync(Guid jobId)
    {
        lock (_gate) _jobs.Add(jobId);
        return InvokeAsync("JoinJob", jobId);
    }

    public Task JoinBranchAsync(string branchKey)
    {
        lock (_gate) _branches.Add(branchKey);
        return InvokeAsync("JoinBranch", branchKey);
    }

    /// <summary>Join the global scope group to receive branch/instance CRUD events for the whole system.</summary>
    public Task JoinScopeAsync()
    {
        lock (_gate) _scope = true;
        return InvokeAsync("JoinScope");
    }

    public Task JoinInstanceAsync(string branchKey, string instanceKey)
    {
        lock (_gate) _instances.Add((branchKey, instanceKey));
        return InvokeAsync("JoinInstance", branchKey, instanceKey);
    }

    public Task JoinTriggerAsync(Guid triggerId)
    {
        lock (_gate) _triggers.Add(triggerId);
        return InvokeAsync("JoinTrigger", triggerId);
    }

    public Task LeaveTriggerAsync(Guid triggerId)
    {
        lock (_gate) _triggers.Remove(triggerId);
        return InvokeAsync("LeaveTrigger", triggerId);
    }

    private Task InvokeAsync(string method, params object?[] args) =>
        _connection?.InvokeCoreAsync(method, args) ?? Task.CompletedTask;

    private async Task RejoinAllAsync()
    {
        if (_connection is null) return;
        Guid[] jobs; string[] branches; (string Branch, string Instance)[] instances; Guid[] triggers; bool scope;
        lock (_gate)
        {
            jobs = [.. _jobs];
            branches = [.. _branches];
            instances = [.. _instances];
            triggers = [.. _triggers];
            scope = _scope;
        }
        try
        {
            if (scope) await _connection.InvokeAsync("JoinScope");
            foreach (var b in branches) await _connection.InvokeAsync("JoinBranch", b);
            foreach (var (branch, instance) in instances) await _connection.InvokeAsync("JoinInstance", branch, instance);
            foreach (var j in jobs) await _connection.InvokeAsync("JoinJob", j);
            foreach (var t in triggers) await _connection.InvokeAsync("JoinTrigger", t);
        }
        catch
        {
            // Best-effort: if the connection drops again mid-rejoin, the next Reconnected re-applies.
        }
    }

    public async Task StopAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
            _connection = null;
        }
    }

    /// <summary>Reconnect indefinitely (the built-in policy gives up after ~30s): fast at first, then steady.</summary>
    private sealed class ForeverRetryPolicy : IRetryPolicy
    {
        public TimeSpan? NextRetryDelay(RetryContext ctx) => ctx.PreviousRetryCount switch
        {
            0 => TimeSpan.Zero,
            < 5 => TimeSpan.FromSeconds(2),
            < 10 => TimeSpan.FromSeconds(5),
            _ => TimeSpan.FromSeconds(15),
        };
    }
}
