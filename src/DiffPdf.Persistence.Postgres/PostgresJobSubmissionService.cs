using DiffPdf.Core.Models;
using DiffPdf.Persistence;
using Wolverine.EntityFrameworkCore;

namespace DiffPdf.Persistence.Postgres;

/// <summary>
/// Transactional outbox: inserts the job and enqueues the command in the same
/// database transaction via Wolverine's EF Core outbox, then flushes it to the
/// durable local queue. The job and its message are persisted atomically — neither
/// can exist without the other.
/// </summary>
public sealed class PostgresJobSubmissionService(IDbContextOutbox<DiffPdfDbContext> outbox) : IJobSubmissionService
{
    public async Task SubmitAsync(ComparisonJob job, object command, CancellationToken ct = default)
    {
        outbox.DbContext.Jobs.Add(PostgresJobStore.ToEntity(job));
        await outbox.PublishAsync(command);
        await outbox.SaveChangesAndFlushMessagesAsync();
    }
}
