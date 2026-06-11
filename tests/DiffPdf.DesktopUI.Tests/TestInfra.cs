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
    public IReadOnlyList<FilePairTaskSummary>? Tasks { get; set; } // GET /jobs/{id}/tasks (filtered + paged below)
    public Func<string, object?>? InstancesByBranch { get; init; }
    public object? Readiness { get; init; }
    public object? Stats { get; init; }
    public object? Triggers { get; init; }

    /// <summary>Requests whose path ends with this suffix block until <see cref="Release"/> is called.</summary>
    public string? GatedSuffix { get; init; }

    /// <summary>Catch-all override checked first: return a payload to serve it (200), or null to fall through.</summary>
    public Func<HttpRequestMessage, object?>? Custom { get; set; }

    private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void Release() => _gate.TrySetResult();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (GatedSuffix is not null && path.EndsWith(GatedSuffix, StringComparison.Ordinal))
            await _gate.Task.ConfigureAwait(false);

        if (Custom?.Invoke(request) is { } custom)
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(custom, custom.GetType(), Json), Encoding.UTF8, "application/json"),
            };

        // GET /jobs/{id}/tasks — the server owns the file-list filter (name search + "only differing") and paging,
        // returning the matching page plus the filtered total in X-Total-Count. Mirror that so a view-model test
        // can assert the VM loads exactly what the server returns for the query it sent.
        if (Tasks is not null && path.EndsWith("/tasks", StringComparison.Ordinal))
            return PagedTasks(request.RequestUri!.Query);

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

    // Applies the same name-search + "only differing" filter and limit/offset paging the real /tasks endpoint
    // does, then returns the page with the filtered total in X-Total-Count (what the client reads for the count).
    private HttpResponseMessage PagedTasks(string query)
    {
        var q = ParseQuery(query);
        IEnumerable<FilePairTaskSummary> match = Tasks!;
        if (q.GetValueOrDefault("search") is { Length: > 0 } search)
            match = match.Where(t => t.RelativePath.Contains(search, StringComparison.OrdinalIgnoreCase));
        if (q.GetValueOrDefault("onlyDiffering") == "true")
            match = match.Where(t => t.ResultStatus is "Differs" or "OnlyInOld" or "OnlyInNew" or "Error");

        var all = match.ToList();
        var page = all.Skip(QueryInt(q, "offset", 0)).Take(QueryInt(q, "limit", all.Count)).ToList();
        var resp = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(page, Json), Encoding.UTF8, "application/json"),
        };
        resp.Headers.TryAddWithoutValidation("X-Total-Count", all.Count.ToString());
        return resp;
    }

    private static int QueryInt(IReadOnlyDictionary<string, string> q, string key, int fallback) =>
        q.TryGetValue(key, out var v) && int.TryParse(v, out var n) ? n : fallback;

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split('=', 2);
            result[Uri.UnescapeDataString(kv[0])] = kv.Length > 1 ? Uri.UnescapeDataString(kv[1]) : "";
        }
        return result;
    }
}

/// <summary>Reflection helpers to drive a view-model's private members from tests without widening its API.</summary>
internal static class VmTest
{
    public static void SetField(object target, string field, object? value) =>
        target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    public static Task InvokeAsync(object target, string method) =>
        (Task)target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, null)!;

    public static Task InvokeAsync(object target, string method, params object?[] args) =>
        (Task)target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args)!;

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
