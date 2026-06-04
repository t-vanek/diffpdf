using System.Diagnostics;
using System.Reflection;
using DiffPdf.Core.Abstractions;
using DiffPdf.Core.Models;
using DiffPdf.Core.Storage;
using DiffPdf.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace DiffPdf.Api.Operational;

/// <summary>The configured persistence backend name, for the operational status view.</summary>
public sealed record PersistenceInfo(string Provider);

/// <summary>The effective auth state (<c>Auth:Enabled</c> AND a relational DB is configured), surfaced
/// anonymously on <c>/health</c> so clients can decide whether to prompt for credentials.</summary>
public sealed record ServerAuthInfo(bool AuthEnabled);

/// <summary>Process build version + start time, shared by the liveness probe and the status view.</summary>
public static class BuildInfo
{
    public static readonly DateTimeOffset StartedAt = DateTimeOffset.UtcNow;

    public static readonly string Version =
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString()
        ?? "unknown";

    public static double UptimeSeconds => (DateTimeOffset.UtcNow - StartedAt).TotalSeconds;
}

/// <summary>
/// Composes the operational status / readiness documents from three sources: the heartbeat registry
/// (this replica's services), the leader lease + job backlog (the shared database), and dependency
/// probes (database, renderer, storage). Registered as a singleton; resolves the scoped stores via
/// <see cref="IServiceScopeFactory"/> per call and caches the (process-spawning) renderer probe.
/// </summary>
public sealed class OperationalStatusService(
    IServiceScopeFactory scopeFactory,
    ILeaderElection leader,
    IWorkerInstanceIdProvider worker,
    IAutomationHeartbeat heartbeat,
    IEnumerable<IPdfPageRenderer> renderers,
    PersistenceInfo persistence,
    IOptions<StorageOptions> storage,
    IHostEnvironment environment)
{
    // Canonical automation services, so the status always lists them even before their first tick.
    private static readonly string[] KnownServices = ["control-plane", "stale-recovery"];

    private static readonly TimeSpan RendererCacheTtl = TimeSpan.FromSeconds(60);
    private readonly SemaphoreSlim _rendererGate = new(1, 1);
    private IReadOnlyList<RendererHealth>? _rendererCache;
    private DateTimeOffset _rendererCachedAt;

    /// <summary>The full operational dashboard (authenticated endpoint).</summary>
    public async Task<OperationalStatusResponse> BuildStatusAsync(CancellationToken ct = default)
    {
        var replica = new ReplicaInfo(worker.WorkerInstanceId, BuildInfo.Version, environment.EnvironmentName, BuildInfo.StartedAt, BuildInfo.UptimeSeconds);
        var leaderInfo = await BuildLeaderAsync(ct);
        var services = BuildServices();

        BacklogInfo backlog;
        DependencyCheck database;
        int enabledChecks;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var sp = scope.ServiceProvider;
            (backlog, database) = await BuildBacklogAsync(sp.GetRequiredService<IJobStore>(), sp.GetRequiredService<IFilePairTaskStore>(), ct);
            enabledChecks = (await sp.GetRequiredService<IControlCheckStore>().ListEnabledAsync(ct)).Count;
        }

        var dependencies = new DependenciesInfo(database, await CheckRendererAsync(ct), CheckStorage());

        return new OperationalStatusResponse(
            replica, leaderInfo, services, backlog, enabledChecks, dependencies);
    }

    /// <summary>Readiness: are the critical dependencies usable? Returns whether ready plus the per-check breakdown.</summary>
    public async Task<(bool Ready, ReadinessResponse Body)> BuildReadinessAsync(CancellationToken ct = default)
    {
        DependencyCheck database;
        await using (var scope = scopeFactory.CreateAsyncScope())
            database = await CheckDatabaseAsync(scope.ServiceProvider.GetRequiredService<IJobStore>(), ct);

        var checks = new[] { database, await CheckRendererAsync(ct), CheckStorage() };
        bool ready = checks.All(c => c.Ok);
        return (ready, new ReadinessResponse(ready ? "ready" : "degraded", checks));
    }

    private async Task<LeaderInfo> BuildLeaderAsync(CancellationToken ct)
    {
        LeaderLease? lease = null;
        try { lease = await leader.GetAsync(AutomationLeader.Role, ct); }
        catch { /* a leader-read failure must not break the whole status document */ }

        bool isThisReplica = lease is not null && lease.Owner == worker.WorkerInstanceId;
        bool healthy = lease is not null && lease.ExpiresAt > DateTimeOffset.UtcNow;
        return new LeaderInfo(AutomationLeader.Role, isThisReplica, lease?.Owner, lease?.AcquiredAt, lease?.RenewedAt, lease?.ExpiresAt, healthy);
    }

    private IReadOnlyList<ServiceHealthInfo> BuildServices()
    {
        var snapshot = heartbeat.Snapshot().ToDictionary(h => h.Service, StringComparer.Ordinal);
        return KnownServices.Select(name =>
            snapshot.TryGetValue(name, out var h)
                ? new ServiceHealthInfo(h.Service, h.LastTickAt, h.LastLeaderActiveAt, h.TickCount, h.LastError, h.LastErrorAt)
                : new ServiceHealthInfo(name, null, null, 0, null, null)).ToList();
    }

    private async Task<(BacklogInfo, DependencyCheck)> BuildBacklogAsync(IJobStore jobs, IFilePairTaskStore tasks, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var counts = await jobs.CountByStatusAsync(ct);
            int active = await tasks.CountActiveAsync(ct);
            sw.Stop();
            int Count(JobStatus s) => counts.TryGetValue(s, out var n) ? n : 0;
            return (new BacklogInfo(Count(JobStatus.Queued), Count(JobStatus.Running), Count(JobStatus.Paused), active),
                    new DependencyCheck("database", true, $"{persistence.Provider}, {sw.ElapsedMilliseconds} ms"));
        }
        catch (Exception ex)
        {
            return (new BacklogInfo(0, 0, 0, 0), new DependencyCheck("database", false, $"{persistence.Provider}: {ex.Message}"));
        }
    }

    private async Task<DependencyCheck> CheckDatabaseAsync(IJobStore jobs, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await jobs.CountByStatusAsync(ct);
            sw.Stop();
            return new DependencyCheck("database", true, $"{persistence.Provider}, {sw.ElapsedMilliseconds} ms");
        }
        catch (Exception ex)
        {
            return new DependencyCheck("database", false, $"{persistence.Provider}: {ex.Message}");
        }
    }

    private async Task<DependencyCheck> CheckRendererAsync(CancellationToken ct)
    {
        var healths = await GetRendererHealthsAsync(ct);
        if (healths.Count == 0)
            return new DependencyCheck("renderer", false, "no renderer registered");
        bool ok = healths.Any(h => h.Available);
        string detail = string.Join(", ", healths.Select(h => $"{h.Backend}={(h.Available ? h.Version ?? "ok" : "unavailable")}"));
        return new DependencyCheck("renderer", ok, detail);
    }

    private async Task<IReadOnlyList<RendererHealth>> GetRendererHealthsAsync(CancellationToken ct)
    {
        if (_rendererCache is not null && DateTimeOffset.UtcNow - _rendererCachedAt < RendererCacheTtl)
            return _rendererCache;

        await _rendererGate.WaitAsync(ct);
        try
        {
            if (_rendererCache is not null && DateTimeOffset.UtcNow - _rendererCachedAt < RendererCacheTtl)
                return _rendererCache;

            var list = new List<RendererHealth>();
            foreach (var r in renderers)
                list.Add(await r.CheckAsync(ct));
            _rendererCache = list;
            _rendererCachedAt = DateTimeOffset.UtcNow;
            return list;
        }
        finally
        {
            _rendererGate.Release();
        }
    }

    private DependencyCheck CheckStorage()
    {
        string root = storage.Value.RootPath;
        try
        {
            Directory.CreateDirectory(root);
            string probe = Path.Combine(root, $".diffpdf-probe-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return new DependencyCheck("storage", true, root);
        }
        catch (Exception ex)
        {
            return new DependencyCheck("storage", false, $"{root}: {ex.Message}");
        }
    }
}
