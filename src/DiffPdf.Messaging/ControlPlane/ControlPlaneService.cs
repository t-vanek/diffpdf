using DiffPdf.Core.Abstractions;
using DiffPdf.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DiffPdf.Messaging.ControlPlane;

/// <summary>
/// The unified control/monitoring runner. Every tick it re-reads the enabled checks from
/// <see cref="IControlCheckStore"/> (a fresh DI scope) and reconciles them through a
/// <see cref="ControlCheckReconciler"/>, running each due check via <see cref="IControlCheckRunner"/>,
/// so checks created/edited/deleted via the API take effect within one tick — no restart. Idle (but
/// alive) when there are no enabled checks. Replaces the former scheduler / folder-watch / scope-sync /
/// retention background services with one mechanism.
/// </summary>
/// <remarks>
/// Multi-replica safe: each tick first acquires/renews the shared <see cref="AutomationLeader"/> lease
/// via <see cref="ILeaderElection"/>; only the leading replica reconciles and runs, so a check fires
/// once across the cluster (the in-memory fallback always leads). On leader death a stand-by takes over
/// within ~<see cref="AutomationLeader.Lease"/>; its reconciler re-seeds without firing.
/// </remarks>
public sealed class ControlPlaneService(
    IServiceScopeFactory scopeFactory,
    ILeaderElection leader,
    IWorkerInstanceIdProvider workerInstance,
    IAutomationHeartbeat heartbeat,
    IOptions<ControlPlaneOptions> options,
    ILogger<ControlPlaneService> logger) : BackgroundService
{
    private const string ServiceName = "control-plane";

    private readonly ControlCheckReconciler _reconciler = new();
    private bool _wasLeader;
    private bool _provisioned;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(5, options.Value.TickSeconds));
        logger.LogInformation("Control-plane runner started (DB-backed, {Interval}s tick).", interval.TotalSeconds);

        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                heartbeat.Record(ServiceName, false, ex.Message);
                logger.LogError(ex, "Control-plane tick failed.");
            }
        }
    }

    internal async Task TickAsync(CancellationToken ct)
    {
        // Only the automation leader reconciles + runs, so each check fires once across replicas.
        bool isLeader = await leader.TryAcquireAsync(AutomationLeader.Role, workerInstance.WorkerInstanceId, AutomationLeader.Lease, ct);
        if (isLeader != _wasLeader)
        {
            logger.LogInformation(isLeader
                ? "This replica is now the automation leader (control-plane active)."
                : "This replica is no longer the automation leader (control-plane idle).");
            _wasLeader = isLeader;
        }
        heartbeat.Record(ServiceName, isLeader);
        if (!isLeader)
            return;

        await using var scope = scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IControlCheckStore>();
        var runner = scope.ServiceProvider.GetRequiredService<IControlCheckRunner>();

        // Once per process, on the first tick we lead: create the baseline checks and a readiness check for
        // every existing branch. Doing it here (not in a StartAsync hosted service) avoids racing the EF
        // migrator and runs once across the cluster. Best-effort; the flag is set only on success so a
        // transient failure is retried next tick.
        if (!_provisioned)
        {
            await scope.ServiceProvider.GetRequiredService<IControlCheckProvisioner>()
                .ProvisionBaselineAndExistingAsync(ct);
            _provisioned = true;
        }

        var checks = await store.ListEnabledAsync(ct);
        var due = _reconciler.Reconcile(
            DateTime.UtcNow, checks,
            (c, error) => logger.LogWarning("Ignoring control check {Key}: {Error}.", c.Key, error));

        foreach (var check in due)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await runner.RunAsync(check, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Control check {Key} run failed.", check.Key);
            }
        }
    }
}
