using DiffPdf.Core.Abstractions;
using DiffPdf.Messaging.Messages;
using DiffPdf.Persistence;
using DiffPdf.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wolverine;

namespace DiffPdf.Messaging;

/// <summary>
/// Periodically requeues file-pair tasks whose worker lease expired (crashed
/// mid-comparison) and re-dispatches them, so a batch resumes instead of
/// stalling. Recovery is safe: re-dispatched pairs are idempotent (claim +
/// complete-once), and a finished job's CompleteAsync is a no-op.
/// </summary>
public sealed class StaleTaskRecoveryService(
    IServiceScopeFactory scopeFactory,
    IAutomationHeartbeat heartbeat,
    IOptions<WorkerOptions> options,
    ILogger<StaleTaskRecoveryService> logger) : BackgroundService
{
    private const string ServiceName = "stale-recovery";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.StaleRecoveryInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var taskStore = scope.ServiceProvider.GetRequiredService<IFilePairTaskStore>();
                var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();

                var recovered = await taskStore.RequeueStaleAsync(stoppingToken);
                foreach (var (jobId, taskId) in recovered)
                    await bus.PublishAsync(new CompareFilePair(jobId, taskId));

                if (recovered.Count > 0)
                    logger.LogInformation("Recovered {Count} stale file-pair task(s)", recovered.Count);

                // Not leader-gated — runs on every replica — so each tick is "active work".
                heartbeat.Record(ServiceName, leaderActive: true);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                heartbeat.Record(ServiceName, false, ex.Message);
                logger.LogError(ex, "Stale task recovery pass failed");
            }
        }
    }
}
