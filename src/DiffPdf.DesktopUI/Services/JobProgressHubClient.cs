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

    public event Action<JobProgress>? ProgressReceived;

    /// <summary>Raised (on the UI thread) for each trigger / batch / comparison lifecycle event.</summary>
    public event Action<TriggerEvent>? TriggerEventReceived;

    /// <summary>Raised (on the UI thread) for each per-branch run-queue state push.</summary>
    public event Action<BranchQueueState>? QueueStateReceived;

    public bool IsConnected => _connection is { State: HubConnectionState.Connected };

    public async Task EnsureStartedAsync()
    {
        if (_connection is not null || session.BaseUrl is null)
            return;

        var url = session.BaseUrl.TrimEnd('/') + "/hubs/jobs";
        _connection = new HubConnectionBuilder()
            .WithUrl(url, o => o.AccessTokenProvider = () => tokens.GetTokenAsync())
            .WithAutomaticReconnect()
            .Build();

        _connection.On<JobProgress>("jobProgress", p =>
            Dispatcher.UIThread.Post(() => ProgressReceived?.Invoke(p)));

        _connection.On<TriggerEvent>("triggerEvent", e =>
            Dispatcher.UIThread.Post(() => TriggerEventReceived?.Invoke(e)));

        _connection.On<BranchQueueState>("queueState", s =>
            Dispatcher.UIThread.Post(() => QueueStateReceived?.Invoke(s)));

        // After a reconnect the server-side groups are gone (new connection id) — re-join everything we track.
        _connection.Reconnected += _ => RejoinAllAsync();

        await _connection.StartAsync();
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
        Guid[] jobs; string[] branches; (string Branch, string Instance)[] instances; Guid[] triggers;
        lock (_gate)
        {
            jobs = [.. _jobs];
            branches = [.. _branches];
            instances = [.. _instances];
            triggers = [.. _triggers];
        }
        try
        {
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
}
