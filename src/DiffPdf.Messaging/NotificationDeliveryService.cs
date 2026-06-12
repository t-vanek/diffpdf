using DiffPdf.Application.Abstractions;
using DiffPdf.Core.Abstractions;
using DiffPdf.Notifications;
using DiffPdf.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DiffPdf.Messaging;

/// <summary>
/// Leader-gated sender for the notification outbox: every tick it runs one
/// <see cref="NotificationDeliveryPump"/> pass over the due <see cref="Core.Models.NotificationDelivery"/>
/// rows (the pump owns the retry/backoff/dead-letter semantics; this service owns the timer and the leader
/// gate). Mirrors <see cref="JobStalledWatchdogService"/>'s leader-gating (same <see cref="AutomationLeader"/>
/// lease) so exactly one replica sends mail.
/// </summary>
public sealed class NotificationDeliveryService(
    IServiceScopeFactory scopeFactory,
    ILeaderElection leader,
    IWorkerInstanceIdProvider workerInstance,
    IAutomationHeartbeat heartbeat,
    DiffPdfMetrics metrics,
    IOptions<NotificationDeliveryOptions> options,
    ILogger<NotificationDeliveryService> logger) : BackgroundService
{
    private const string ServiceName = "notification-delivery";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = options.Value;
        if (!opts.Enabled)
        {
            logger.LogInformation("Notification delivery service disabled.");
            return;
        }

        logger.LogInformation("Notification delivery service started ({Interval}s tick, {Max} attempts max).",
            opts.Interval.TotalSeconds, opts.MaxAttempts);
        using var timer = new PeriodicTimer(opts.Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await TickAsync(opts, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                heartbeat.Record(ServiceName, false, ex.Message);
                logger.LogError(ex, "Notification delivery tick failed.");
            }
        }
    }

    private async Task TickAsync(NotificationDeliveryOptions opts, CancellationToken ct)
    {
        bool isLeader = await leader.TryAcquireAsync(AutomationLeader.Role, workerInstance.WorkerInstanceId, AutomationLeader.Lease, ct);
        heartbeat.Record(ServiceName, isLeader);
        if (!isLeader)
            return;

        await using var scope = scopeFactory.CreateAsyncScope();
        var pump = new NotificationDeliveryPump(
            scope.ServiceProvider.GetRequiredService<INotificationDeliveryStore>(),
            scope.ServiceProvider.GetRequiredService<EmailSettingsResolver>(),
            scope.ServiceProvider.GetRequiredService<IEmailSender>(),
            scope.ServiceProvider.GetService<ISystemEventLog>() ?? new NullSystemEventLog(),
            metrics, opts, logger);
        await pump.DeliverDueAsync(DateTimeOffset.UtcNow, ct);
    }
}
