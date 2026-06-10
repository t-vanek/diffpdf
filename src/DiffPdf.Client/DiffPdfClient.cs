using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.SignalR.Client;

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

    /// <summary>Lists branches with their instance count + run-queue state in one call (no per-branch round-trips).</summary>
    public Task<IReadOnlyList<BranchSummary>> ListBranchSummariesAsync(CancellationToken ct = default) =>
        JsonAsync<IReadOnlyList<BranchSummary>>(HttpMethod.Get, "/api/v1/branches/summary", null, ct);

    public Task<Branch?> GetBranchAsync(string branchKey, CancellationToken ct = default) =>
        GetOrNullAsync<Branch>($"/api/v1/branches/{Esc(branchKey)}", ct);

    /// <summary>Updates a branch's name/enabled. Throws DiffPdfApiException 404 if unknown, 409 on version conflict.</summary>
    public Task<Branch> UpdateBranchAsync(string branchKey, UpdateBranchRequest request, CancellationToken ct = default) =>
        JsonAsync<Branch>(HttpMethod.Put, $"/api/v1/branches/{Esc(branchKey)}", request, ct);

    /// <summary>
    /// Deletes a branch. Without <paramref name="cascade"/>, throws DiffPdfApiException 409 if it has instances
    /// or an active job; with <paramref name="cascade"/> = true, also deletes its instances + jobs. 404 if unknown.
    /// </summary>
    public async Task DeleteBranchAsync(string branchKey, bool cascade = false, CancellationToken ct = default)
    {
        var url = $"/api/v1/branches/{Esc(branchKey)}" + (cascade ? "?cascade=true" : "");
        using var resp = await SendRawAsync(HttpMethod.Delete, url, null, ct);
    }

    /// <summary>Aggregated statistics for a branch (jobs, checks, automations, comparisons).</summary>
    public Task<ScopeStatsResponse> GetBranchStatsAsync(string branchKey, CancellationToken ct = default) =>
        JsonAsync<ScopeStatsResponse>(HttpMethod.Get, $"/api/v1/branches/{Esc(branchKey)}/stats", null, ct);

    // ---------------- Instances ----------------

    public Task<CreatedInstanceResponse> CreateInstanceAsync(
        string branchKey, CreateInstanceRequest request, bool ensureStructure = true, CancellationToken ct = default) =>
        JsonAsync<CreatedInstanceResponse>(
            HttpMethod.Post, $"/api/v1/branches/{Esc(branchKey)}/instances?ensureStructure={(ensureStructure ? "true" : "false")}", request, ct);

    public Task<IReadOnlyList<Instance>> ListInstancesAsync(string branchKey, CancellationToken ct = default) =>
        JsonAsync<IReadOnlyList<Instance>>(HttpMethod.Get, $"/api/v1/branches/{Esc(branchKey)}/instances", null, ct);

    public Task<Instance?> GetInstanceAsync(string branchKey, string instanceKey, CancellationToken ct = default) =>
        GetOrNullAsync<Instance>($"/api/v1/branches/{Esc(branchKey)}/instances/{Esc(instanceKey)}", ct);

    /// <summary>Updates an instance's name/basePath/credentialProfile/enabled. Throws 404 if unknown, 409 on version conflict.</summary>
    public Task<Instance> UpdateInstanceAsync(string branchKey, string instanceKey, UpdateInstanceRequest request, CancellationToken ct = default) =>
        JsonAsync<Instance>(HttpMethod.Put, $"/api/v1/branches/{Esc(branchKey)}/instances/{Esc(instanceKey)}", request, ct);

    /// <summary>Deletes an instance. Throws DiffPdfApiException 409 if it has any jobs; 404 if unknown.</summary>
    public async Task DeleteInstanceAsync(string branchKey, string instanceKey, CancellationToken ct = default)
    {
        using var resp = await SendRawAsync(HttpMethod.Delete, $"/api/v1/branches/{Esc(branchKey)}/instances/{Esc(instanceKey)}", null, ct);
    }

    /// <summary>Aggregated statistics for an instance (jobs, checks, automations, comparisons).</summary>
    public Task<ScopeStatsResponse> GetInstanceStatsAsync(string branchKey, string instanceKey, CancellationToken ct = default) =>
        JsonAsync<ScopeStatsResponse>(HttpMethod.Get, $"/api/v1/branches/{Esc(branchKey)}/instances/{Esc(instanceKey)}/stats", null, ct);

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

    /// <summary>
    /// Detect the on-disk <c>&lt;root&gt;/&lt;branch&gt;/&lt;instance&gt;</c> tree and reconcile it with the database.
    /// Dry-run (report only) unless <paramref name="apply"/> is true, in which case branches/instances are
    /// registered and missing folders are created.
    /// </summary>
    public Task<ScopeSyncReport> SyncScopeAsync(bool apply = false, CancellationToken ct = default) =>
        JsonAsync<ScopeSyncReport>(HttpMethod.Post, $"/api/v1/scope/sync?apply={(apply ? "true" : "false")}", null, ct);

    /// <summary>The server's configured structure root (branches/instances are created under it as &lt;root&gt;/&lt;branch&gt;/&lt;instance&gt;); Configured=false when unset.</summary>
    public Task<ScopeRootInfo> GetScopeRootAsync(CancellationToken ct = default) =>
        JsonAsync<ScopeRootInfo>(HttpMethod.Get, "/api/v1/scope/root", null, ct);

    // ---------------- Jobs (observation + control) ----------------

    public Task<IReadOnlyList<JobSummary>> ListJobsAsync(
        string? branchKey = null, string? instanceKey = null, JobStatus? status = null,
        int? limit = null, int? offset = null, CancellationToken ct = default)
    {
        var q = new List<string>();
        if (branchKey is not null) q.Add($"branchKey={Esc(branchKey)}");
        if (instanceKey is not null) q.Add($"instanceKey={Esc(instanceKey)}");
        if (status is { } st) q.Add($"status={st}");
        if (limit is { } l) q.Add($"limit={l}");
        if (offset is { } o) q.Add($"offset={o}");
        string url = "/api/v1/jobs" + (q.Count > 0 ? "?" + string.Join("&", q) : "");
        return JsonAsync<IReadOnlyList<JobSummary>>(HttpMethod.Get, url, null, ct);
    }

    /// <summary>Like <see cref="ListJobsAsync"/> but also returns the total matching count (from the
    /// <c>X-Total-Count</c> header) for the desktop's lazy "load more on scroll".</summary>
    public async Task<(IReadOnlyList<JobSummary> Items, int Total)> ListJobsPagedAsync(
        string? branchKey, string? instanceKey, JobStatus? status, int limit, int offset, CancellationToken ct = default)
    {
        var q = new List<string> { $"limit={limit}", $"offset={offset}" };
        if (branchKey is not null) q.Add($"branchKey={Esc(branchKey)}");
        if (instanceKey is not null) q.Add($"instanceKey={Esc(instanceKey)}");
        if (status is { } st) q.Add($"status={st}");
        using var resp = await SendRawAsync(HttpMethod.Get, "/api/v1/jobs?" + string.Join("&", q), null, ct);
        var items = (await resp.Content.ReadFromJsonAsync<IReadOnlyList<JobSummary>>(Json, ct))!;
        return (items, ReadTotalCount(resp, items.Count));
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

    // Finished reports are immutable and the server tags them (ETag derived from the job version): remember
    // the last few per job and revalidate with If-None-Match — a 304 skips re-downloading a multi-MB body.
    private readonly ConcurrentDictionary<Guid, (string ETag, BatchComparisonReport Report)> _reportCache = new();

    public async Task<BatchComparisonReport> GetReportAsync(Guid id, CancellationToken ct = default)
    {
        bool hasCached = _reportCache.TryGetValue(id, out var cached);
        using var req = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/jobs/{id}/report");
        if (hasCached) req.Headers.TryAddWithoutValidation("If-None-Match", cached.ETag);

        using var resp = await http.SendAsync(req, ct);
        if (hasCached && resp.StatusCode == HttpStatusCode.NotModified)
            return cached.Report;
        if (!resp.IsSuccessStatusCode) throw await ApiException(resp, ct);

        var report = (await resp.Content.ReadFromJsonAsync<BatchComparisonReport>(Json, ct))!;
        if (resp.Headers.ETag?.Tag is { } etag)
        {
            if (_reportCache.Count >= 8) _reportCache.Clear(); // tiny memo — a desktop session revisits few jobs
            _reportCache[id] = (etag, report);
        }
        return report;
    }

    public Task<IReadOnlyList<FilePairTaskSummary>> GetTasksAsync(Guid id, CancellationToken ct = default) =>
        JsonAsync<IReadOnlyList<FilePairTaskSummary>>(HttpMethod.Get, $"/api/v1/jobs/{id}/tasks", null, ct);

    /// <summary>A page of a job's file-pair tasks for the lazy-loaded file list — returns the page plus the
    /// filtered total (<c>X-Total-Count</c>). Optional name <paramref name="search"/> + <paramref name="onlyDiffering"/>
    /// filter run server-side, so they cover unloaded pairs too.</summary>
    public async Task<(IReadOnlyList<FilePairTaskSummary> Items, int Total)> GetTasksAsync(
        Guid id, int limit, int offset, string? search = null, bool onlyDiffering = false, CancellationToken ct = default)
    {
        var url = $"/api/v1/jobs/{id}/tasks?limit={limit}&offset={offset}"
                + (string.IsNullOrWhiteSpace(search) ? "" : $"&search={Esc(search)}")
                + (onlyDiffering ? "&onlyDiffering=true" : "");
        using var resp = await SendRawAsync(HttpMethod.Get, url, null, ct);
        var items = (await resp.Content.ReadFromJsonAsync<IReadOnlyList<FilePairTaskSummary>>(Json, ct))!;
        return (items, ReadTotalCount(resp, items.Count));
    }

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

    /// <summary>
    /// Streams a highlighted diff PDF artifact into <paramref name="destination"/> without buffering the whole
    /// file in memory — for save-to-disk flows. The byte[] overload remains for in-memory rendering.
    /// </summary>
    public async Task DownloadArtifactAsync(Guid id, string relativePath, Stream destination, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/jobs/{id}/artifacts/{relativePath.TrimStart('/')}");
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode) throw await ApiException(resp, ct);
        await resp.Content.CopyToAsync(destination, ct);
    }

    /// <summary>Polls a job to a terminal state and returns its report. Throws if it ends Failed/Cancelled.
    /// Without an explicit <paramref name="pollInterval"/> the poll backs off progressively (1 s → 5 s cap) —
    /// a long batch does not deserve a request every second; an explicit interval is honoured as-is.</summary>
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
            if (pollInterval is null)
                delay = TimeSpan.FromTicks(Math.Min((long)(delay.Ticks * 1.5), TimeSpan.FromSeconds(5).Ticks));
        }
    }

    // ---------------- Realtime job progress (SignalR) ----------------

    /// <summary>
    /// Opens a SignalR connection to the server's job-progress hub, joins the given job's group and invokes
    /// <paramref name="onProgress"/> for each push. Dispose the returned <see cref="HubConnection"/> to stop.
    /// For an authenticated server supply <paramref name="accessTokenProvider"/>. Live progress is a
    /// notification channel only — REST (<see cref="GetJobAsync"/>) stays the source of truth, so a missed
    /// event can be recovered by reloading the job.
    /// </summary>
    public async Task<HubConnection> SubscribeToJobProgressAsync(
        Guid jobId, Action<JobProgress> onProgress,
        Func<Task<string?>>? accessTokenProvider = null, CancellationToken ct = default)
    {
        if (http.BaseAddress is null)
            throw new InvalidOperationException("The HttpClient has no BaseAddress to derive the hub URL from.");

        var hubUrl = new Uri($"{http.BaseAddress.GetLeftPart(UriPartial.Authority)}/hubs/jobs");
        var connection = new HubConnectionBuilder()
            .WithUrl(hubUrl, o => { if (accessTokenProvider is not null) o.AccessTokenProvider = accessTokenProvider; })
            .WithAutomaticReconnect()
            .Build();

        connection.On<JobProgress>("jobProgress", onProgress);
        await connection.StartAsync(ct);
        await connection.InvokeAsync("JoinJob", jobId, ct);
        return connection;
    }

    /// <summary>
    /// Opens a SignalR connection, joins a trigger's group and invokes <paramref name="onEvent"/> for each
    /// <c>triggerEvent</c> push (trigger / batch / comparison lifecycle). Dispose the connection to stop.
    /// </summary>
    public async Task<HubConnection> SubscribeToTriggerEventsAsync(
        Guid triggerId, Action<TriggerEvent> onEvent,
        Func<Task<string?>>? accessTokenProvider = null, CancellationToken ct = default)
    {
        if (http.BaseAddress is null)
            throw new InvalidOperationException("The HttpClient has no BaseAddress to derive the hub URL from.");

        var hubUrl = new Uri($"{http.BaseAddress.GetLeftPart(UriPartial.Authority)}/hubs/jobs");
        var connection = new HubConnectionBuilder()
            .WithUrl(hubUrl, o => { if (accessTokenProvider is not null) o.AccessTokenProvider = accessTokenProvider; })
            .WithAutomaticReconnect()
            .Build();

        connection.On<TriggerEvent>("triggerEvent", onEvent);
        await connection.StartAsync(ct);
        await connection.InvokeAsync("JoinTrigger", triggerId, ct);
        return connection;
    }

    // ---------------- Control checks ----------------

    public Task<CheckResponse> CreateCheckAsync(CreateCheckRequest request, CancellationToken ct = default) =>
        JsonAsync<CheckResponse>(HttpMethod.Post, "/api/v1/checks", request, ct);

    public Task<IReadOnlyList<CheckResponse>> ListChecksAsync(CancellationToken ct = default) =>
        JsonAsync<IReadOnlyList<CheckResponse>>(HttpMethod.Get, "/api/v1/checks", null, ct);

    public Task<CheckResponse?> GetCheckAsync(Guid id, CancellationToken ct = default) =>
        GetOrNullAsync<CheckResponse>($"/api/v1/checks/{id}", ct);

    public Task<CheckResponse> UpdateCheckAsync(Guid id, UpdateCheckRequest request, CancellationToken ct = default) =>
        JsonAsync<CheckResponse>(HttpMethod.Put, $"/api/v1/checks/{id}", request, ct);

    public async Task DeleteCheckAsync(Guid id, CancellationToken ct = default)
    {
        using var resp = await SendRawAsync(HttpMethod.Delete, $"/api/v1/checks/{id}", null, ct);
    }

    /// <summary>Runs a control check now and returns the recorded run.</summary>
    public Task<CheckRunResponse> RunCheckAsync(Guid id, CancellationToken ct = default) =>
        JsonAsync<CheckRunResponse>(HttpMethod.Post, $"/api/v1/checks/{id}/run", null, ct);

    /// <summary>Run history of a control check (newest first).</summary>
    public Task<IReadOnlyList<CheckRunResponse>> ListCheckRunsAsync(Guid id, int? limit = null, CancellationToken ct = default) =>
        JsonAsync<IReadOnlyList<CheckRunResponse>>(
            HttpMethod.Get, $"/api/v1/checks/{id}/runs" + (limit is { } l ? $"?limit={l}" : ""), null, ct);

    // ---------------- Triggers (Spouštěče) ----------------

    public Task<IReadOnlyList<TriggerResponse>> ListTriggersAsync(
        Guid? instanceId = null, Guid? branchId = null, TriggerStatus? status = null, bool includeDeleted = false, CancellationToken ct = default)
    {
        var q = new List<string>();
        if (instanceId is { } i) q.Add($"instanceId={i}");
        if (branchId is { } b) q.Add($"branchId={b}");
        if (status is { } s) q.Add($"status={s}");
        if (includeDeleted) q.Add("includeDeleted=true");
        string url = "/api/v1/triggers" + (q.Count > 0 ? "?" + string.Join("&", q) : "");
        return JsonAsync<IReadOnlyList<TriggerResponse>>(HttpMethod.Get, url, null, ct);
    }

    public Task<TriggerResponse?> GetTriggerAsync(Guid id, CancellationToken ct = default) =>
        GetOrNullAsync<TriggerResponse>($"/api/v1/triggers/{id}", ct);

    public Task<TriggerResponse> CreateTriggerAsync(CreateTriggerRequest request, CancellationToken ct = default) =>
        JsonAsync<TriggerResponse>(HttpMethod.Post, "/api/v1/triggers", request, ct);

    public Task<TriggerResponse> UpdateTriggerAsync(Guid id, UpdateTriggerRequest request, CancellationToken ct = default) =>
        JsonAsync<TriggerResponse>(HttpMethod.Patch, $"/api/v1/triggers/{id}", request, ct);

    /// <summary>Soft-deletes a trigger (history preserved). Throws 404 if unknown.</summary>
    public async Task DeleteTriggerAsync(Guid id, CancellationToken ct = default)
    {
        using var resp = await SendRawAsync(HttpMethod.Delete, $"/api/v1/triggers/{id}", null, ct);
    }

    public Task<TriggerResponse> EnableTriggerAsync(Guid id, CancellationToken ct = default) =>
        JsonAsync<TriggerResponse>(HttpMethod.Post, $"/api/v1/triggers/{id}/enable", null, ct);

    public Task<TriggerResponse> DisableTriggerAsync(Guid id, CancellationToken ct = default) =>
        JsonAsync<TriggerResponse>(HttpMethod.Post, $"/api/v1/triggers/{id}/disable", null, ct);

    /// <summary>
    /// Runs a trigger: creates + enqueues a batch job and returns its id without waiting. Returns the result
    /// for both 202 (queued) and 4xx (validation), so callers inspect Success/ErrorCode; only unexpected statuses throw.
    /// Pass <paramref name="idempotencyKey"/> to dedupe retries (a repeat returns the original batch job).
    /// </summary>
    public async Task<RunTriggerResponse> RunTriggerAsync(Guid id, string? idempotencyKey = null, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/triggers/{id}/run");
        if (!string.IsNullOrWhiteSpace(idempotencyKey)) req.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        using var resp = await http.SendAsync(req, ct);
        if (resp.StatusCode is HttpStatusCode.Accepted or HttpStatusCode.OK or HttpStatusCode.NotFound
            or HttpStatusCode.Conflict or HttpStatusCode.UnprocessableEntity)
            return (await resp.Content.ReadFromJsonAsync<RunTriggerResponse>(Json, ct))!;
        throw await ApiException(resp, ct);
    }

    public Task<IReadOnlyList<TriggerRunResponse>> ListTriggerRunsAsync(Guid id, int? limit = null, CancellationToken ct = default) =>
        JsonAsync<IReadOnlyList<TriggerRunResponse>>(HttpMethod.Get, $"/api/v1/triggers/{id}/runs" + (limit is { } l ? $"?limit={l}" : ""), null, ct);

    public Task<IReadOnlyList<JobSummary>> ListTriggerJobsAsync(Guid id, CancellationToken ct = default) =>
        JsonAsync<IReadOnlyList<JobSummary>>(HttpMethod.Get, $"/api/v1/triggers/{id}/jobs", null, ct);

    public Task<IReadOnlyList<AuditEntryResponse>> ListTriggerAuditAsync(Guid id, CancellationToken ct = default) =>
        JsonAsync<IReadOnlyList<AuditEntryResponse>>(HttpMethod.Get, $"/api/v1/triggers/{id}/audit", null, ct);

    /// <summary>Lists a branch's audit entries (created / updated / deleted), newest first.</summary>
    public Task<IReadOnlyList<AuditEntryResponse>> ListBranchAuditAsync(string branchKey, CancellationToken ct = default) =>
        JsonAsync<IReadOnlyList<AuditEntryResponse>>(HttpMethod.Get, $"/api/v1/branches/{Esc(branchKey)}/audit", null, ct);

    public Task<IReadOnlyList<TriggerResponse>> ListInstanceTriggersAsync(Guid instanceId, CancellationToken ct = default) =>
        JsonAsync<IReadOnlyList<TriggerResponse>>(HttpMethod.Get, $"/api/v1/instances/{instanceId}/triggers", null, ct);

    public Task<IReadOnlyList<TriggerResponse>> ListBranchTriggersAsync(Guid branchId, CancellationToken ct = default) =>
        JsonAsync<IReadOnlyList<TriggerResponse>>(HttpMethod.Get, $"/api/v1/branches/{branchId}/triggers", null, ct);

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

    // ---------------- E-mail (SMTP) settings ----------------

    public Task<EmailSettingsResponse> GetEmailSettingsAsync(CancellationToken ct = default) =>
        JsonAsync<EmailSettingsResponse>(HttpMethod.Get, "/api/v1/settings/email", null, ct);

    public Task<EmailSettingsResponse> UpdateEmailSettingsAsync(UpdateEmailSettingsRequest request, CancellationToken ct = default) =>
        JsonAsync<EmailSettingsResponse>(HttpMethod.Put, "/api/v1/settings/email", request, ct);

    /// <summary>Sends a test e-mail using the saved SMTP settings; throws <see cref="DiffPdfApiException"/> on failure.</summary>
    public async Task SendTestEmailAsync(SendTestEmailRequest request, CancellationToken ct = default)
    {
        using var resp = await SendRawAsync(HttpMethod.Post, "/api/v1/settings/email/test", request, ct);
    }

    // ---------------- Discovery / single comparison / health ----------------

    public Task<NetworkConfigSummary> ListSharesAsync(CancellationToken ct = default) =>
        JsonAsync<NetworkConfigSummary>(HttpMethod.Get, "/api/v1/discovery/shares", null, ct);

    // ---------------- Triggers ----------------

    /// <summary>Triggers a batch for one instance now (create + start). Outcome is Launched / NothingToCompare / Unreachable; 404 throws.</summary>
    public Task<TriggerResult> TriggerBatchAsync(string branchKey, string instanceKey, CancellationToken ct = default) =>
        JsonAsync<TriggerResult>(HttpMethod.Post, $"/api/v1/triggers/{Esc(branchKey)}/{Esc(instanceKey)}", null, ct);

    /// <summary>Triggers a batch for every enabled instance under a branch (fan-out).</summary>
    public Task<BranchRunResult> RunBranchAsync(string branchKey, CancellationToken ct = default) =>
        JsonAsync<BranchRunResult>(HttpMethod.Post, $"/api/v1/branches/{Esc(branchKey)}/run", null, ct);

    /// <summary>Compares a single old/new pair synchronously. Returns the raw result JSON (the per-page model is deep).</summary>
    public Task<JsonElement> CompareSingleAsync(SingleComparisonRequest request, CancellationToken ct = default) =>
        JsonAsync<JsonElement>(HttpMethod.Post, "/api/v1/comparisons", request, ct);

    /// <summary>Compares a single pair and returns the verdict together with the highlighted diff PDF bytes
    /// (streamed as <c>application/pdf</c>; <see cref="SingleComparisonPreview.DiffPdf"/> is null when the
    /// documents are identical). Throws <see cref="DiffPdfApiException"/> 422 when a file cannot be compared.</summary>
    public async Task<SingleComparisonPreview> CompareSinglePreviewAsync(SingleComparisonRequest request, CancellationToken ct = default)
    {
        using var resp = await SendRawAsync(HttpMethod.Post, "/api/v1/comparisons/preview", request, ct);
        return await ReadPreviewAsync(resp, ct);
    }

    /// <summary>Compares two PDFs uploaded as bytes (multipart) — for when the server runs on a different machine
    /// than the client, so a server-side path would not resolve. Returns the verdict + highlighted diff PDF bytes
    /// (null when identical). Throws <see cref="DiffPdfApiException"/> 422 when a file cannot be compared.</summary>
    public async Task<SingleComparisonPreview> CompareSinglePreviewUploadAsync(
        byte[] oldPdf, string oldName, byte[] newPdf, string newName, ComparisonOptions options, CancellationToken ct = default)
    {
        using var form = new MultipartFormDataContent
        {
            // Parts WITH a filename bind as files (form.Files["old"/"new"]); the filename also names the diff PDF.
            { new ByteArrayContent(oldPdf), "old", string.IsNullOrWhiteSpace(oldName) ? "old.pdf" : oldName },
            { new ByteArrayContent(newPdf), "new", string.IsNullOrWhiteSpace(newName) ? "new.pdf" : newName },
            // No filename → binds as a form field (form["options"]).
            { new StringContent(JsonSerializer.Serialize(options, Json)), "options" },
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/comparisons/preview-upload") { Content = form };
        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) throw await ApiException(resp, ct);
        return await ReadPreviewAsync(resp, ct);
    }

    private static async Task<SingleComparisonPreview> ReadPreviewAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        byte[]? pdf = null;
        if (resp.StatusCode != HttpStatusCode.NoContent)
        {
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
            if (bytes.Length > 0) pdf = bytes;
        }
        return new SingleComparisonPreview
        {
            Identical = string.Equals(Header(resp, "X-Diff-Identical"), "true", StringComparison.OrdinalIgnoreCase),
            OldPageCount = HeaderInt(resp, "X-Diff-Old-Pages"),
            NewPageCount = HeaderInt(resp, "X-Diff-New-Pages"),
            DifferingPages = HeaderInt(resp, "X-Diff-Differing"),
            Similarity = double.TryParse(Header(resp, "X-Diff-Similarity"), NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s : double.NaN,
            ContentErrorCount = HeaderInt(resp, "X-Diff-Content-Errors"),
            DiffPdf = pdf,
        };
    }

    private static string? Header(HttpResponseMessage resp, string name)
    {
        if (resp.Headers.TryGetValues(name, out var values))
            foreach (var v in values) return v;
        return null;
    }

    private static int HeaderInt(HttpResponseMessage resp, string name) =>
        int.TryParse(Header(resp, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : 0;

    /// <summary>Liveness probe; true when the API responds 200.</summary>
    public async Task<bool> HealthAsync(CancellationToken ct = default)
    {
        using var resp = await http.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/health"), ct);
        return resp.IsSuccessStatusCode;
    }

    /// <summary>Reads the anonymous <c>/health</c> body (version, uptime, whether auth is required).
    /// Returns null if the server does not respond 200 (unreachable or error), so callers can probe safely.</summary>
    public async Task<ServerInfo?> GetServerInfoAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await http.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/health"), ct);
            return resp.IsSuccessStatusCode
                ? await resp.Content.ReadFromJsonAsync<ServerInfo>(Json, ct)
                : null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    /// <summary>Readiness: dependency checks (database / renderer / storage). Returns the body for both 200 (ready) and 503 (degraded).</summary>
    public async Task<ReadinessResponse> GetReadinessAsync(CancellationToken ct = default)
    {
        using var resp = await http.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/health/ready"), ct);
        if (resp.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.ServiceUnavailable))
            throw await ApiException(resp, ct);
        return (await resp.Content.ReadFromJsonAsync<ReadinessResponse>(Json, ct))!;
    }

    /// <summary>Full operational status (authenticated): leader, service heartbeats, backlog, dependencies.</summary>
    public Task<OperationalStatusResponse> GetStatusAsync(CancellationToken ct = default) =>
        JsonAsync<OperationalStatusResponse>(HttpMethod.Get, "/api/v1/status", null, ct);

    // ---------------- Per-branch run queue ----------------

    /// <summary>Get a branch's run-queue state (per-branch hold + per-instance status), or null if the branch is unknown.</summary>
    public Task<BranchQueueState?> GetBranchQueueAsync(string branchKey, CancellationToken ct = default) =>
        GetOrNullAsync<BranchQueueState>($"/api/v1/branches/{Esc(branchKey)}/queue", ct);

    /// <summary>Run a queue action (enqueue/run/pause/resume/stop) for a single instance; returns the updated state + note.</summary>
    public Task<QueueActionOutcome> QueueInstanceAsync(string branchKey, string instanceKey, QueueAction action, CancellationToken ct = default) =>
        JsonAsync<QueueActionOutcome>(HttpMethod.Post, $"/api/v1/branches/{Esc(branchKey)}/instances/{Esc(instanceKey)}/queue", new QueueActionRequest(action), ct);

    /// <summary>Run a queue action for a whole branch (cascades to all enabled instances); returns the updated state + note.</summary>
    public Task<QueueActionOutcome> QueueBranchAsync(string branchKey, QueueAction action, CancellationToken ct = default) =>
        JsonAsync<QueueActionOutcome>(HttpMethod.Post, $"/api/v1/branches/{Esc(branchKey)}/queue", new QueueActionRequest(action), ct);

    /// <summary>
    /// Opens a SignalR connection, joins a branch's group and invokes <paramref name="onState"/> for each
    /// <c>queueState</c> push. Dispose the returned <see cref="HubConnection"/> to stop.
    /// </summary>
    public async Task<HubConnection> SubscribeToBranchQueueAsync(
        string branchKey, Action<BranchQueueState> onState, Func<Task<string?>>? accessTokenProvider = null, CancellationToken ct = default)
    {
        if (http.BaseAddress is null)
            throw new InvalidOperationException("The HttpClient has no BaseAddress to derive the hub URL from.");

        var hubUrl = new Uri($"{http.BaseAddress.GetLeftPart(UriPartial.Authority)}/hubs/jobs");
        var connection = new HubConnectionBuilder()
            .WithUrl(hubUrl, o => { if (accessTokenProvider is not null) o.AccessTokenProvider = accessTokenProvider; })
            .WithAutomaticReconnect()
            .Build();

        connection.On<BranchQueueState>("queueState", onState);
        await connection.StartAsync(ct);
        await connection.InvokeAsync("JoinBranch", branchKey, ct);
        return connection;
    }

    // ---------------- Scope configuration (triggers + comparers) ----------------

    /// <summary>Get the global trigger + comparer configuration (stored + server-resolved effective).</summary>
    public Task<ScopeConfigResponse> GetGlobalConfigAsync(CancellationToken ct = default) =>
        JsonAsync<ScopeConfigResponse>(HttpMethod.Get, "/api/v1/config/global", null, ct);

    /// <summary>Update the global trigger + comparer configuration.</summary>
    public Task<ScopeConfigResponse> UpdateGlobalConfigAsync(UpsertScopeConfigRequest request, CancellationToken ct = default) =>
        JsonAsync<ScopeConfigResponse>(HttpMethod.Put, "/api/v1/config/global", request, ct);

    /// <summary>Get a branch's configuration (stored + effective), or null when the branch does not exist.</summary>
    public Task<ScopeConfigResponse?> GetBranchConfigAsync(string branchKey, CancellationToken ct = default) =>
        GetOrNullAsync<ScopeConfigResponse>($"/api/v1/branches/{Esc(branchKey)}/config", ct);

    /// <summary>Update a branch's configuration.</summary>
    public Task<ScopeConfigResponse> UpdateBranchConfigAsync(string branchKey, UpsertScopeConfigRequest request, CancellationToken ct = default) =>
        JsonAsync<ScopeConfigResponse>(HttpMethod.Put, $"/api/v1/branches/{Esc(branchKey)}/config", request, ct);

    /// <summary>Get an instance's configuration (stored + effective), or null when the instance does not exist.</summary>
    public Task<ScopeConfigResponse?> GetInstanceConfigAsync(string branchKey, string instanceKey, CancellationToken ct = default) =>
        GetOrNullAsync<ScopeConfigResponse>($"/api/v1/branches/{Esc(branchKey)}/instances/{Esc(instanceKey)}/config", ct);

    /// <summary>Update an instance's configuration.</summary>
    public Task<ScopeConfigResponse> UpdateInstanceConfigAsync(string branchKey, string instanceKey, UpsertScopeConfigRequest request, CancellationToken ct = default) =>
        JsonAsync<ScopeConfigResponse>(HttpMethod.Put, $"/api/v1/branches/{Esc(branchKey)}/instances/{Esc(instanceKey)}/config", request, ct);

    /// <summary>Get a branch's scheduled-run setting, or null when the branch does not exist.</summary>
    public Task<ScheduleResponse?> GetBranchScheduleAsync(string branchKey, CancellationToken ct = default) =>
        GetOrNullAsync<ScheduleResponse>($"/api/v1/branches/{Esc(branchKey)}/schedule", ct);

    /// <summary>Enable/disable a branch's scheduled run.</summary>
    public Task<ScheduleResponse> SetBranchScheduleAsync(string branchKey, SetScheduleRequest request, CancellationToken ct = default) =>
        JsonAsync<ScheduleResponse>(HttpMethod.Put, $"/api/v1/branches/{Esc(branchKey)}/schedule", request, ct);

    /// <summary>Get an instance's scheduled-run setting, or null when the instance does not exist.</summary>
    public Task<ScheduleResponse?> GetInstanceScheduleAsync(string branchKey, string instanceKey, CancellationToken ct = default) =>
        GetOrNullAsync<ScheduleResponse>($"/api/v1/branches/{Esc(branchKey)}/instances/{Esc(instanceKey)}/schedule", ct);

    /// <summary>Enable/disable an instance's scheduled run.</summary>
    public Task<ScheduleResponse> SetInstanceScheduleAsync(string branchKey, string instanceKey, SetScheduleRequest request, CancellationToken ct = default) =>
        JsonAsync<ScheduleResponse>(HttpMethod.Put, $"/api/v1/branches/{Esc(branchKey)}/instances/{Esc(instanceKey)}/schedule", request, ct);

    // ---------------- plumbing ----------------

    private sealed record JobActionResult(int Resumed, int Retried, JobSummary Job);

    private async Task<T> JsonAsync<T>(HttpMethod method, string url, object? body, CancellationToken ct)
    {
        using var resp = await SendRawAsync(method, url, body, ct);
        return (await resp.Content.ReadFromJsonAsync<T>(Json, ct))!;
    }

    private static int ReadTotalCount(HttpResponseMessage resp, int fallback)
    {
        if (resp.Headers.TryGetValues("X-Total-Count", out var values))
            foreach (var v in values)
                if (int.TryParse(v, out var n)) return n;
        return fallback;
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
