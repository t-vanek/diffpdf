using Avalonia.Threading;
using DiffPdf.Client;
using Microsoft.AspNetCore.SignalR.Client;

namespace DiffPdf.DesktopUI.Services;

/// <summary>
/// Wraps a SignalR <see cref="HubConnection"/> to the server's <c>/hubs/jobs</c> hub. Joins job/branch/
/// instance/trigger groups and raises <see cref="ProgressReceived"/> / <see cref="TriggerEventReceived"/>
/// (on the UI thread) for each <c>jobProgress</c> / <c>triggerEvent</c> push. Connects anonymously when
/// auth is off, otherwise with a bearer token from <see cref="TokenSource"/>.
/// </summary>
public sealed class JobProgressHubClient(ServerSession session, TokenSource tokens)
{
    private HubConnection? _connection;

    public event Action<JobProgress>? ProgressReceived;

    /// <summary>Raised (on the UI thread) for each trigger / batch / comparison lifecycle event.</summary>
    public event Action<TriggerEvent>? TriggerEventReceived;

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

        await _connection.StartAsync();
    }

    public Task JoinJobAsync(Guid jobId) =>
        _connection?.InvokeAsync("JoinJob", jobId) ?? Task.CompletedTask;

    public Task JoinBranchAsync(string branchKey) =>
        _connection?.InvokeAsync("JoinBranch", branchKey) ?? Task.CompletedTask;

    public Task JoinInstanceAsync(string branchKey, string instanceKey) =>
        _connection?.InvokeAsync("JoinInstance", branchKey, instanceKey) ?? Task.CompletedTask;

    public Task JoinTriggerAsync(Guid triggerId) =>
        _connection?.InvokeAsync("JoinTrigger", triggerId) ?? Task.CompletedTask;

    public Task LeaveTriggerAsync(Guid triggerId) =>
        _connection?.InvokeAsync("LeaveTrigger", triggerId) ?? Task.CompletedTask;

    public async Task StopAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
            _connection = null;
        }
    }
}
