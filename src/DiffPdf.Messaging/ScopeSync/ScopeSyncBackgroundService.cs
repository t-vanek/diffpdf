using DiffPdf.Core.Abstractions;
using DiffPdf.Messaging.Scheduling;
using DiffPdf.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DiffPdf.Messaging.ScopeSync;

/// <summary>
/// Periodically (and once on startup) reconciles the configured scope root with the database via
/// <see cref="IScopeSyncService"/>. Multi-replica safe: each tick acquires/renews the shared
/// <see cref="AutomationLeader"/> lease and only the leading replica syncs. No-op while
/// <see cref="ScopeSyncOptions.Enabled"/> is false or no root is configured.
/// </summary>
public sealed class ScopeSyncBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILeaderElection leader,
    IWorkerInstanceIdProvider workerInstance,
    IAutomationHeartbeat heartbeat,
    IOptions<ScopeSyncOptions> options,
    ILogger<ScopeSyncBackgroundService> logger) : BackgroundService
{
    private const string ServiceName = "scope-sync";
    private bool _wasLeader;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opt = options.Value;
        if (!opt.Enabled || string.IsNullOrWhiteSpace(opt.RootPath))
        {
            logger.LogInformation("Scope sync disabled (set ScopeSync:Enabled=true and ScopeSync:RootPath to enable).");
            return;
        }

        var interval = TimeSpan.FromSeconds(Math.Max(30, opt.IntervalSeconds));
        logger.LogInformation("Scope sync started (root {Root}, every {Interval}s).", opt.RootPath, interval.TotalSeconds);

        // Immediate first pass (covers the "at startup" trigger), then on the interval.
        await SafeTickAsync(stoppingToken);
        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await SafeTickAsync(stoppingToken);
    }

    private async Task SafeTickAsync(CancellationToken ct)
    {
        try
        {
            await TickAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            heartbeat.Record(ServiceName, false, ex.Message);
            logger.LogError(ex, "Scope sync tick failed.");
        }
    }

    internal async Task TickAsync(CancellationToken ct)
    {
        bool isLeader = await leader.TryAcquireAsync(AutomationLeader.Role, workerInstance.WorkerInstanceId, AutomationLeader.Lease, ct);
        if (isLeader != _wasLeader)
        {
            logger.LogInformation(isLeader
                ? "This replica is now the automation leader (scope sync active)."
                : "This replica is no longer the automation leader (scope sync idle).");
            _wasLeader = isLeader;
        }
        heartbeat.Record(ServiceName, isLeader);
        if (!isLeader)
            return;

        await using var scope = scopeFactory.CreateAsyncScope();
        var sync = scope.ServiceProvider.GetRequiredService<IScopeSyncService>();
        var report = await sync.SynchronizeAsync(apply: true, ct);

        if (!report.Ok)
            logger.LogWarning("Scope sync not OK: {Error}", report.Error ?? "one or more entries could not be reconciled");
        else if (report.RegisteredBranches > 0 || report.RegisteredInstances > 0 || report.MissingFolders.Count > 0)
            logger.LogInformation("Scope sync: {Branches} branch(es) and {Instances} instance(s) registered; {Missing} missing folder(s) reconciled.",
                report.RegisteredBranches, report.RegisteredInstances, report.MissingFolders.Count);
    }
}
