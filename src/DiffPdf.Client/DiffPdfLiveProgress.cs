using DiffPdf.Core.Abstractions;
using Microsoft.AspNetCore.SignalR.Client;

namespace DiffPdf.Client;

/// <summary>
/// Realtime job progress over SignalR. Subscribe to <see cref="ProgressReceived"/>,
/// then join a job / project / business-instance. REST remains the source of truth,
/// so a missed event can always be reconciled with <see cref="DiffPdfClient.GetJobAsync"/>.
/// </summary>
public sealed class DiffPdfLiveProgress : IAsyncDisposable
{
    private readonly HubConnection _connection;

    private DiffPdfLiveProgress(HubConnection connection)
    {
        _connection = connection;
        _connection.On<JobProgressChanged>("jobProgress", p => ProgressReceived?.Invoke(p));
    }

    /// <summary>Raised for every progress event in a joined group.</summary>
    public event Action<JobProgressChanged>? ProgressReceived;

    /// <summary>Underlying connection state (Connected / Reconnecting / Disconnected).</summary>
    public HubConnectionState State => _connection.State;

    /// <summary>Connects to the job-progress hub, optionally supplying a bearer token.</summary>
    public static async Task<DiffPdfLiveProgress> ConnectAsync(
        string hubUrl, Func<Task<string?>>? accessTokenProvider = null, CancellationToken ct = default)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(hubUrl, options =>
            {
                if (accessTokenProvider is not null)
                    options.AccessTokenProvider = accessTokenProvider;
            })
            .WithAutomaticReconnect()
            .Build();

        var live = new DiffPdfLiveProgress(connection);
        await connection.StartAsync(ct);
        return live;
    }

    public Task JoinJobAsync(Guid jobId, CancellationToken ct = default) =>
        _connection.InvokeAsync("JoinJob", jobId, ct);

    public Task LeaveJobAsync(Guid jobId, CancellationToken ct = default) =>
        _connection.InvokeAsync("LeaveJob", jobId, ct);

    public Task JoinProjectAsync(string businessInstanceKey, string projectKey, CancellationToken ct = default) =>
        _connection.InvokeAsync("JoinProject", businessInstanceKey, projectKey, ct);

    public Task JoinBusinessInstanceAsync(string businessInstanceKey, CancellationToken ct = default) =>
        _connection.InvokeAsync("JoinBusinessInstance", businessInstanceKey, ct);

    public ValueTask DisposeAsync() => _connection.DisposeAsync();
}
