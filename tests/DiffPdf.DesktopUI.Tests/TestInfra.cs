using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DiffPdf.Client;

namespace DiffPdf.DesktopUI.Tests;

/// <summary>
/// A fake API transport for driving a real <see cref="DiffPdfClient"/> in view-model tests: it serves canned
/// JSON per route and can gate one route on a manual release, so request timing is fully controlled.
/// </summary>
internal sealed class FakeApi : HttpMessageHandler
{
    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public object? Branches { get; set; } // settable so a test can change the "server" state between reloads
    public object? BranchSummaries { get; set; } // GET /branches/summary
    public object? Jobs { get; set; } // GET /jobs
    public Func<string, object?>? InstancesByBranch { get; init; }
    public object? Readiness { get; init; }
    public object? Stats { get; init; }
    public object? Triggers { get; init; }

    /// <summary>Requests whose path ends with this suffix block until <see cref="Release"/> is called.</summary>
    public string? GatedSuffix { get; init; }

    private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void Release() => _gate.TrySetResult();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (GatedSuffix is not null && path.EndsWith(GatedSuffix, StringComparison.Ordinal))
            await _gate.Task.ConfigureAwait(false);

        object? payload = path switch
        {
            _ when path.EndsWith("/triggers", StringComparison.Ordinal) => Triggers ?? Array.Empty<TriggerResponse>(),
            _ when path.EndsWith("/readiness", StringComparison.Ordinal) => Readiness,
            _ when path.EndsWith("/stats", StringComparison.Ordinal) => Stats,
            _ when path.EndsWith("/instances", StringComparison.Ordinal) => InstancesByBranch?.Invoke(BranchKey(path)),
            _ when path.EndsWith("/branches/summary", StringComparison.Ordinal) => BranchSummaries,
            _ when path.EndsWith("/branches", StringComparison.Ordinal) => Branches,
            _ when path.EndsWith("/jobs", StringComparison.Ordinal) => Jobs,
            _ => null,
        };
        if (payload is null)
            return new HttpResponseMessage(HttpStatusCode.NotFound);

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, payload.GetType(), Json), Encoding.UTF8, "application/json"),
        };
    }

    // /api/v1/branches/{key}/instances
    private static string BranchKey(string path)
    {
        var parts = path.Split('/');
        var i = Array.IndexOf(parts, "branches");
        return i >= 0 && i + 1 < parts.Length ? Uri.UnescapeDataString(parts[i + 1]) : "";
    }
}

/// <summary>Reflection helpers to drive a view-model's private members from tests without widening its API.</summary>
internal static class VmTest
{
    public static void SetField(object target, string field, object? value) =>
        target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    public static Task InvokeAsync(object target, string method) =>
        (Task)target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, null)!;

    public static void Invoke(object target, string method, params object?[] args) =>
        target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
}

/// <summary>
/// Minimal single-threaded SynchronizationContext message pump (Stephen Toub's pattern). Runs the async
/// delegate and every continuation it posts on one thread, modelling the Avalonia UI dispatcher so a
/// view-model's post-await collection rebuilds are serialized (never racing each other).
/// </summary>
internal sealed class AsyncPump : SynchronizationContext
{
    private readonly BlockingCollection<(SendOrPostCallback Cb, object? State)> _queue = new();

    public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));
    public override void Send(SendOrPostCallback d, object? state) => throw new NotSupportedException();

    public static void Run(Func<Task> root)
    {
        var previous = Current;
        var pump = new AsyncPump();
        SetSynchronizationContext(pump);
        try
        {
            var task = root();
            task.ContinueWith(_ => pump._queue.CompleteAdding(), TaskScheduler.Default);
            foreach (var (cb, state) in pump._queue.GetConsumingEnumerable())
                cb(state);
            task.GetAwaiter().GetResult(); // surface assertion failures / exceptions
        }
        finally
        {
            SetSynchronizationContext(previous);
        }
    }
}
