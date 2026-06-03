using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiffPdf.Client;

/// <summary>
/// Typed client for the diffpdf REST API. Construct directly with an
/// <see cref="HttpClient"/> whose <c>BaseAddress</c> is the API root, or register
/// it with <c>services.AddDiffPdfClient(...)</c>.
/// </summary>
public sealed class DiffPdfClient(HttpClient http)
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    // ---------------- Branches ----------------

    public Task<Branch> CreateBranchAsync(CreateBranchRequest request, CancellationToken ct = default) =>
        JsonAsync<Branch>(HttpMethod.Post, "/api/v1/branches", request, ct);

    public Task<IReadOnlyList<Branch>> ListBranchesAsync(CancellationToken ct = default) =>
        JsonAsync<IReadOnlyList<Branch>>(HttpMethod.Get, "/api/v1/branches", null, ct);

    public Task<Branch?> GetBranchAsync(string branchKey, CancellationToken ct = default) =>
        GetOrNullAsync<Branch>($"/api/v1/branches/{Esc(branchKey)}", ct);

    // ---------------- Instances ----------------

    public Task<CreatedInstanceResponse> CreateInstanceAsync(
        string branchKey, CreateInstanceRequest request, bool ensureStructure = true, CancellationToken ct = default) =>
        JsonAsync<CreatedInstanceResponse>(
            HttpMethod.Post, $"/api/v1/branches/{Esc(branchKey)}/instances?ensureStructure={(ensureStructure ? "true" : "false")}", request, ct);

    public Task<IReadOnlyList<Instance>> ListInstancesAsync(string branchKey, CancellationToken ct = default) =>
        JsonAsync<IReadOnlyList<Instance>>(HttpMethod.Get, $"/api/v1/branches/{Esc(branchKey)}/instances", null, ct);

    public Task<Instance?> GetInstanceAsync(string branchKey, string instanceKey, CancellationToken ct = default) =>
        GetOrNullAsync<Instance>($"/api/v1/branches/{Esc(branchKey)}/instances/{Esc(instanceKey)}", ct);

    /// <summary>Create/repair the old/new/reports structure.</summary>
    public Task<InstanceStructureReport> EnsureStructureAsync(string branchKey, string instanceKey, bool includeFiles = false, CancellationToken ct = default) =>
        JsonAsync<InstanceStructureReport>(
            HttpMethod.Post, $"/api/v1/branches/{Esc(branchKey)}/instances/{Esc(instanceKey)}/structure?includeFiles={(includeFiles ? "true" : "false")}", null, ct);

    /// <summary>
    /// Batch readiness: the folder-skeleton state (<see cref="InstanceReadiness.Structure"/>:
    /// old/new/reports + PDF counts, optionally the full file list via <paramref name="includeFiles"/>),
    /// how old/new pair up, and whether a job may be submitted.
    /// </summary>
    public Task<InstanceReadiness> GetReadinessAsync(
        string branchKey, string instanceKey, int? sampleSize = null, bool includeFiles = false, CancellationToken ct = default)
    {
        var q = new List<string>();
        if (sampleSize is { } s) q.Add($"sampleSize={s}");
        if (includeFiles) q.Add("includeFiles=true");
        string url = $"/api/v1/branches/{Esc(branchKey)}/instances/{Esc(instanceKey)}/readiness"
            + (q.Count > 0 ? "?" + string.Join("&", q) : "");
        return JsonAsync<InstanceReadiness>(HttpMethod.Get, url, null, ct);
    }

    // ---------------- Jobs (observation + control) ----------------

    public Task<IReadOnlyList<JobSummary>> ListJobsAsync(
        string? branchKey = null, string? instanceKey = null, JobStatus? status = null, CancellationToken ct = default)
    {
        var q = new List<string>();
        if (branchKey is not null) q.Add($"branchKey={Esc(branchKey)}");
        if (instanceKey is not null) q.Add($"instanceKey={Esc(instanceKey)}");
        if (status is { } st) q.Add($"status={st}");
        string url = "/api/v1/jobs" + (q.Count > 0 ? "?" + string.Join("&", q) : "");
        return JsonAsync<IReadOnlyList<JobSummary>>(HttpMethod.Get, url, null, ct);
    }

    public Task<JobSummary?> GetJobAsync(Guid id, CancellationToken ct = default) =>
        GetOrNullAsync<JobSummary>($"/api/v1/jobs/{id}", ct);

    public Task<JobSummary> PauseJobAsync(Guid id, CancellationToken ct = default) =>
        JsonAsync<JobSummary>(HttpMethod.Post, $"/api/v1/jobs/{id}/pause", null, ct);

    /// <summary>Resumes a paused job; returns the job after re-dispatching its pending pairs.</summary>
    public async Task<JobSummary> ResumeJobAsync(Guid id, CancellationToken ct = default) =>
        (await JsonAsync<JobActionResult>(HttpMethod.Post, $"/api/v1/jobs/{id}/resume", null, ct)).Job;

    public Task<JobSummary> CancelJobAsync(Guid id, CancellationToken ct = default) =>
        JsonAsync<JobSummary>(HttpMethod.Post, $"/api/v1/jobs/{id}/cancel", null, ct);

    /// <summary>Re-runs the failed file-pairs of a finished job; returns the reopened job.</summary>
    public async Task<JobSummary> RetryJobAsync(Guid id, CancellationToken ct = default) =>
        (await JsonAsync<JobActionResult>(HttpMethod.Post, $"/api/v1/jobs/{id}/retry", null, ct)).Job;

    public Task<BatchComparisonReport> GetReportAsync(Guid id, CancellationToken ct = default) =>
        JsonAsync<BatchComparisonReport>(HttpMethod.Get, $"/api/v1/jobs/{id}/report", null, ct);

    public Task<IReadOnlyList<FilePairTaskSummary>> GetTasksAsync(Guid id, CancellationToken ct = default) =>
        JsonAsync<IReadOnlyList<FilePairTaskSummary>>(HttpMethod.Get, $"/api/v1/jobs/{id}/tasks", null, ct);

    /// <summary>CI-gate verdict. Returns the result for both pass (200) and fail (422) — only other statuses throw.</summary>
    public async Task<JobResult> GetResultAsync(Guid id, CancellationToken ct = default)
    {
        using var resp = await http.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"/api/v1/jobs/{id}/result"), ct);
        if (resp.StatusCode is HttpStatusCode.OK or HttpStatusCode.UnprocessableEntity)
            return (await resp.Content.ReadFromJsonAsync<JobResult>(Json, ct))!;
        throw await ApiException(resp, ct);
    }

    /// <summary>Downloads a highlighted diff PDF artifact (relative path from the report's HighlightedPdfPath).</summary>
    public async Task<byte[]> DownloadArtifactAsync(Guid id, string relativePath, CancellationToken ct = default)
    {
        using var resp = await SendRawAsync(HttpMethod.Get, $"/api/v1/jobs/{id}/artifacts/{relativePath.TrimStart('/')}", null, ct);
        return await resp.Content.ReadAsByteArrayAsync(ct);
    }

    /// <summary>Polls a job to a terminal state and returns its report. Throws if it ends Failed/Cancelled.</summary>
    public async Task<BatchComparisonReport> WaitForReportAsync(Guid jobId, TimeSpan? pollInterval = null, CancellationToken ct = default)
    {
        var delay = pollInterval ?? TimeSpan.FromSeconds(1);
        while (true)
        {
            var job = await GetJobAsync(jobId, ct) ?? throw new InvalidOperationException($"Job {jobId} disappeared.");
            switch (job.Status)
            {
                case JobStatus.Completed:
                    return await GetReportAsync(jobId, ct);
                case JobStatus.Failed:
                case JobStatus.Cancelled:
                    throw new DiffPdfApiException(HttpStatusCode.Conflict, job.Error, $"Job {jobId} ended {job.Status}: {job.Error}");
            }
            await Task.Delay(delay, ct);
        }
    }

    // ---------------- Schedules ----------------

    public Task<ScheduleResponse> CreateScheduleAsync(string branchKey, string instanceKey, CreateScheduleRequest request, CancellationToken ct = default) =>
        JsonAsync<ScheduleResponse>(HttpMethod.Post, SchedulesUrl(branchKey, instanceKey), request, ct);

    public Task<IReadOnlyList<ScheduleResponse>> ListSchedulesAsync(string branchKey, string instanceKey, CancellationToken ct = default) =>
        JsonAsync<IReadOnlyList<ScheduleResponse>>(HttpMethod.Get, SchedulesUrl(branchKey, instanceKey), null, ct);

    public Task<ScheduleResponse?> GetScheduleAsync(string branchKey, string instanceKey, string scheduleKey, CancellationToken ct = default) =>
        GetOrNullAsync<ScheduleResponse>($"{SchedulesUrl(branchKey, instanceKey)}/{Esc(scheduleKey)}", ct);

    public Task<ScheduleResponse> UpdateScheduleAsync(string branchKey, string instanceKey, string scheduleKey, UpdateScheduleRequest request, CancellationToken ct = default) =>
        JsonAsync<ScheduleResponse>(HttpMethod.Put, $"{SchedulesUrl(branchKey, instanceKey)}/{Esc(scheduleKey)}", request, ct);

    public async Task DeleteScheduleAsync(string branchKey, string instanceKey, string scheduleKey, CancellationToken ct = default)
    {
        using var resp = await SendRawAsync(HttpMethod.Delete, $"{SchedulesUrl(branchKey, instanceKey)}/{Esc(scheduleKey)}", null, ct);
    }

    /// <summary>Runs a schedule now; returns the queued job id. Throws DiffPdfApiException (422) when there is nothing to compare.</summary>
    public async Task<Guid> RunScheduleNowAsync(string branchKey, string instanceKey, string scheduleKey, CancellationToken ct = default) =>
        (await JsonAsync<RunScheduleResult>(HttpMethod.Post, $"{SchedulesUrl(branchKey, instanceKey)}/{Esc(scheduleKey)}/run", null, ct)).JobId;

    // ---------------- Notification subscriptions ----------------

    public Task<SubscriptionResponse> CreateSubscriptionAsync(CreateSubscriptionRequest request, CancellationToken ct = default) =>
        JsonAsync<SubscriptionResponse>(HttpMethod.Post, "/api/v1/subscriptions", request, ct);

    public Task<IReadOnlyList<SubscriptionResponse>> ListSubscriptionsAsync(CancellationToken ct = default) =>
        JsonAsync<IReadOnlyList<SubscriptionResponse>>(HttpMethod.Get, "/api/v1/subscriptions", null, ct);

    public Task<SubscriptionResponse?> GetSubscriptionAsync(Guid id, CancellationToken ct = default) =>
        GetOrNullAsync<SubscriptionResponse>($"/api/v1/subscriptions/{id}", ct);

    public Task<SubscriptionResponse> UpdateSubscriptionAsync(Guid id, UpdateSubscriptionRequest request, CancellationToken ct = default) =>
        JsonAsync<SubscriptionResponse>(HttpMethod.Put, $"/api/v1/subscriptions/{id}", request, ct);

    public async Task DeleteSubscriptionAsync(Guid id, CancellationToken ct = default)
    {
        using var resp = await SendRawAsync(HttpMethod.Delete, $"/api/v1/subscriptions/{id}", null, ct);
    }

    private static string SchedulesUrl(string branchKey, string instanceKey) =>
        $"/api/v1/branches/{Esc(branchKey)}/instances/{Esc(instanceKey)}/schedules";

    // ---------------- Discovery / single comparison / health ----------------

    public Task<NetworkConfigSummary> ListSharesAsync(CancellationToken ct = default) =>
        JsonAsync<NetworkConfigSummary>(HttpMethod.Get, "/api/v1/discovery/shares", null, ct);

    /// <summary>Compares a single old/new pair synchronously. Returns the raw result JSON (the per-page model is deep).</summary>
    public Task<JsonElement> CompareSingleAsync(SingleComparisonRequest request, CancellationToken ct = default) =>
        JsonAsync<JsonElement>(HttpMethod.Post, "/api/v1/comparisons", request, ct);

    /// <summary>Liveness probe; true when the API responds 200.</summary>
    public async Task<bool> HealthAsync(CancellationToken ct = default)
    {
        using var resp = await http.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/health"), ct);
        return resp.IsSuccessStatusCode;
    }

    // ---------------- plumbing ----------------

    private sealed record JobActionResult(int Resumed, int Retried, JobSummary Job);

    private sealed record RunScheduleResult(Guid JobId);

    private async Task<T> JsonAsync<T>(HttpMethod method, string url, object? body, CancellationToken ct)
    {
        using var resp = await SendRawAsync(method, url, body, ct);
        return (await resp.Content.ReadFromJsonAsync<T>(Json, ct))!;
    }

    private async Task<T?> GetOrNullAsync<T>(string url, CancellationToken ct) where T : class
    {
        using var resp = await http.SendAsync(new HttpRequestMessage(HttpMethod.Get, url), ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        if (!resp.IsSuccessStatusCode) throw await ApiException(resp, ct);
        return await resp.Content.ReadFromJsonAsync<T>(Json, ct);
    }

    private async Task<HttpResponseMessage> SendRawAsync(HttpMethod method, string url, object? body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, url);
        if (body is not null) req.Content = JsonContent.Create(body, body.GetType(), options: Json);
        var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var ex = await ApiException(resp, ct);
            resp.Dispose();
            throw ex;
        }
        return resp;
    }

    private static async Task<DiffPdfApiException> ApiException(HttpResponseMessage resp, CancellationToken ct)
    {
        string? detail = await SafeReadDetailAsync(resp, ct);
        return new DiffPdfApiException(resp.StatusCode, detail,
            $"diffpdf API {(int)resp.StatusCode} {resp.StatusCode}{(detail is null ? "" : $": {detail}")}");
    }

    private static async Task<string?> SafeReadDetailAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            string text = await resp.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrWhiteSpace(text)) return null;
            try
            {
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String) return d.GetString();
                if (doc.RootElement.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String) return t.GetString();
            }
            catch (JsonException) { /* not JSON */ }
            return text;
        }
        catch { return null; }
    }

    private static string Esc(string segment) => Uri.EscapeDataString(segment);
}
