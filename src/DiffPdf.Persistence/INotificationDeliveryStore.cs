using DiffPdf.Core.Models;

namespace DiffPdf.Persistence;

/// <summary>
/// Persistence for the notification outbox (<see cref="NotificationDelivery"/>): the dispatcher appends rows,
/// the delivery service claims the due ones and records attempt outcomes, and the API reads the history /
/// re-queues dead-lettered rows.
/// </summary>
public interface INotificationDeliveryStore
{
    Task AddRangeAsync(IReadOnlyList<NotificationDelivery> deliveries, CancellationToken ct = default);

    Task<NotificationDelivery?> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>Pending/Failed rows whose <see cref="NotificationDelivery.NextAttemptAt"/> is due, oldest first.</summary>
    Task<IReadOnlyList<NotificationDelivery>> ListDueAsync(DateTimeOffset now, int limit, CancellationToken ct = default);

    /// <summary>Most recent rows (newest first), optionally filtered by status — the UI delivery history.</summary>
    Task<IReadOnlyList<NotificationDelivery>> ListRecentAsync(int limit, NotificationDeliveryStatus? status = null, CancellationToken ct = default);

    Task MarkSentAsync(Guid id, DateTimeOffset sentAt, CancellationToken ct = default);

    /// <summary>Records a failed attempt: schedules the retry, or parks the row as DeadLetter when <paramref name="deadLetter"/>.</summary>
    Task MarkFailedAsync(Guid id, string error, int attemptCount, DateTimeOffset? nextAttemptAt, bool deadLetter, CancellationToken ct = default);

    Task<int> CountAsync(NotificationDeliveryStatus status, CancellationToken ct = default);

    /// <summary>Re-queues a row for delivery now (manual re-send from the UI); resets the attempt counter. False when unknown.</summary>
    Task<bool> RequeueAsync(Guid id, DateTimeOffset now, CancellationToken ct = default);

    /// <summary>Deletes terminal rows (Sent/DeadLetter) older than the cutoff; returns the number removed (retention).</summary>
    Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default);
}
