using System.Collections.Concurrent;
using DiffPdf.Application.Abstractions;
using DiffPdf.Core.Abstractions;
using DiffPdf.Core.Models;
using DiffPdf.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DiffPdf.Messaging.Automations;

/// <summary>
/// The automation engine's scheduler. Every tick it re-reads the enabled automations from
/// <see cref="IAutomationStore"/> (a fresh DI scope), seeds the persisted <c>NextRunAt</c> of schedule
/// automations that lack one (new or migrated rows — never firing immediately), and starts every due
/// automation as a tracked background task under a bounded-concurrency semaphore, so automations
/// created/edited/deleted via the API take effect within one tick — no restart — and one hung run can
/// never block the tick or the other automations. Event-triggered and manual runs do not pass through
/// here; all three share the claim in <c>IAutomationStore.TryBeginRunAsync</c> and the pipeline in
/// <see cref="IAutomationRunner"/>.
/// </summary>
/// <remarks>
/// Multi-replica safe: each tick first acquires/renews the shared <see cref="AutomationLeader"/> lease via
/// <see cref="ILeaderElection"/>; only the leading replica seeds and fires, so a schedule fires once
/// across the cluster (the in-memory fallback always leads). Because <c>NextRunAt</c> is persisted, a
/// stand-by that takes over within ~<see cref="AutomationLeader.Lease"/> continues the existing schedule —
/// an overdue cron still fires once instead of being silently skipped to its next occurrence. The tick
/// period is jittered ±10 % so the engine does not align with the other background services' timers.
/// </remarks>
public sealed class AutomationEngineService(
    IServiceScopeFactory scopeFactory,
    ILeaderElection leader,
    IWorkerInstanceIdProvider workerInstance,
    IAutomationHeartbeat heartbeat,
    AutomationMetrics metrics,
    IOptions<AutomationEngineOptions> options,
    ILogger<AutomationEngineService> logger) : BackgroundService
{
    private const string ServiceName = "automation-engine";

    private readonly ConcurrentDictionary<Guid, Task> _inFlight = new();
    private readonly SemaphoreSlim _slots = new(Math.Max(1, options.Value.MaxConcurrentRuns));
    private bool _wasLeader;
    private bool _provisioned;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var baseInterval = TimeSpan.FromSeconds(Math.Max(5, options.Value.TickSeconds));
        logger.LogInformation(
            "Automation engine started ({Interval}s tick, {Slots} concurrent run slot(s)).",
            baseInterval.TotalSeconds, Math.Max(1, options.Value.MaxConcurrentRuns));

        using var timer = new PeriodicTimer(baseInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                // ±10 % jitter so the engine's tick never marches in lockstep with the other services.
                timer.Period = baseInterval * (0.9 + 0.2 * Random.Shared.NextDouble());
                try
                {
                    await TickAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    heartbeat.Record(ServiceName, false, ex.Message);
                    logger.LogError(ex, "Automation engine tick failed.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutdown
        }

        // Give in-flight runs a short grace so their completion writes (CancellationToken.None) release the
        // claims; anything still hung self-heals later via the stale-claim window.
        var pending = _inFlight.Values.Where(t => !t.IsCompleted).ToArray();
        if (pending.Length > 0)
        {
            try
            {
                await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
            }
            catch
            {
                logger.LogWarning("Automation engine stopped with {Count} run(s) still in flight.", pending.Count(t => !t.IsCompleted));
            }
        }
    }

    internal async Task TickAsync(CancellationToken ct)
    {
        // Only the automation leader seeds + fires, so each schedule fires once across replicas.
        bool isLeader = await leader.TryAcquireAsync(AutomationLeader.Role, workerInstance.WorkerInstanceId, AutomationLeader.Lease, ct);
        if (isLeader != _wasLeader)
        {
            logger.LogInformation(isLeader
                ? "This replica is now the automation leader (engine active)."
                : "This replica is no longer the automation leader (engine idle).");
            _wasLeader = isLeader;
        }
        heartbeat.Record(ServiceName, isLeader);
        if (!isLeader)
            return;

        await using var scope = scopeFactory.CreateAsyncScope();

        // Once per process, on the first tick we lead: create the baseline automations plus the per-branch
        // readiness automation for every existing branch. Doing it here (not in a StartAsync hosted service)
        // avoids racing the EF migrator and runs once across the cluster. Best-effort; the flag is set only
        // on success so a transient failure is retried next tick.
        if (!_provisioned)
        {
            await scope.ServiceProvider.GetRequiredService<IAutomationProvisioner>()
                .ProvisionBaselineAndExistingAsync(ct);
            await scope.ServiceProvider.GetRequiredService<ITriggerProvisioner>()
                .ProvisionExistingAsync(ct);
            await scope.ServiceProvider.GetRequiredService<IScopeConfigurationProvisioner>()
                .ProvisionExistingAsync(ct);
            _provisioned = true;
        }

        var store = scope.ServiceProvider.GetRequiredService<IAutomationStore>();
        var all = await store.ListEnabledAsync(ct);
        var now = DateTimeOffset.UtcNow;

        // Seed the persisted schedule of automations that have none yet (new or migrated rows). Seeding
        // never fires immediately — the first due time is the *next* occurrence.
        foreach (var automation in all.Where(a => a.NextRunAt is null && AutomationCadence.HasSchedule(a.Cron, a.IntervalSeconds)))
        {
            ct.ThrowIfCancellationRequested();
            if (AutomationCadence.TryComputeNext(automation.Cron, automation.IntervalSeconds, now, out var next, out var error)
                && next is { } n)
                await store.SetNextRunIfNullAsync(automation.Id, n, ct);
            else if (error is not null)
                logger.LogWarning("Automation {Key}: invalid cadence ignored ({Error}).", automation.Key, error);
        }

        metrics.SetFailing(all.Count(a => a.FailureThreshold > 0 && a.ConsecutiveFailures >= a.FailureThreshold));

        foreach (var automation in all.Where(a => a.NextRunAt is { } due && due <= now))
        {
            ct.ThrowIfCancellationRequested();
            if (_inFlight.TryGetValue(automation.Id, out var running) && !running.IsCompleted)
                continue; // still running locally from a previous tick

            if (!await _slots.WaitAsync(0, ct))
            {
                logger.LogDebug("All automation run slots busy; remaining due automations wait for the next tick.");
                break; // the rest stays due — NextRunAt is only advanced by a successful claim
            }

            _inFlight[automation.Id] = RunOneTrackedAsync(automation, ct);
        }
    }

    private async Task RunOneTrackedAsync(Automation automation, CancellationToken ct)
    {
        try
        {
            await RunOneAsync(automation, ct);
        }
        finally
        {
            _slots.Release();
            _inFlight.TryRemove(automation.Id, out _);
        }
    }

    private async Task RunOneAsync(Automation automation, CancellationToken ct)
    {
        try
        {
            // One DI scope per run — parallel runs must not share a scoped DbContext.
            await using var scope = scopeFactory.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IAutomationStore>();

            var now = DateTimeOffset.UtcNow;
            AutomationCadence.TryComputeNext(automation.Cron, automation.IntervalSeconds, now, out var next, out _);

            // Atomic claim + schedule advance: a failed claim (manual/event run in flight, or deleted) leaves
            // NextRunAt due, so the schedule simply retries next tick once the other run released the claim.
            if (!await store.TryBeginRunAsync(automation.Id, now, advanceNextRunAtTo: next, AutomationCadence.StaleClaimWindow(automation), ct))
                return;

            var runner = scope.ServiceProvider.GetRequiredService<IAutomationRunner>();
            await runner.RunAsync(automation, AutomationTriggerKind.Schedule, triggerDetail: null, chainDepth: 0, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // shutdown — the runner already released the claim
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Automation {Key}: scheduled run failed unexpectedly.", automation.Key);
        }
    }
}
