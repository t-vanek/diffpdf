using Avalonia.Threading;
using DiffPdf.Client;
using Microsoft.AspNetCore.SignalR.Client;

namespace DiffPdf.DesktopUI.Services;

/// <summary>
/// Wraps a SignalR <see cref="HubConnection"/> to the server's <c>/hubs/jobs</c> hub. Joins job/branch
/// groups and raises <see cref="ProgressReceived"/> (on the UI thread) for each <c>jobProgress</c> push.
/// Connects anonymously when auth is off, otherwise with a bearer token from <see cref="TokenSource"/>.
/// </summary>
public sealed class JobProgressHubClient(ServerSession session, TokenSource tokens)
{
    private HubConnection? _connection;

    public event Action<JobProgress>? ProgressReceived;

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

        await _connection.StartAsync();
    }

    public Task JoinJobAsync(Guid jobId) =>
        _connection?.InvokeAsync("JoinJob", jobId) ?? Task.CompletedTask;

    public Task JoinBranchAsync(string branchKey) =>
        _connection?.InvokeAsync("JoinBranch", branchKey) ?? Task.CompletedTask;

    public Task JoinInstanceAsync(string branchKey, string instanceKey) =>
        _connection?.InvokeAsync("JoinInstance", branchKey, instanceKey) ?? Task.CompletedTask;

    public async Task StopAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
            _connection = null;
        }
    }
}
