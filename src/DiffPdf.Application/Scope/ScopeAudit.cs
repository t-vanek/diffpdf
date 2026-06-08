using DiffPdf.Core.Models;
using DiffPdf.Persistence;

namespace DiffPdf.Application.Scope;

/// <summary>
/// Plumbing shared by <see cref="BranchService"/> and <see cref="InstanceService"/>: the append-only audit
/// entry builder for branch/instance CRUD performed via the manager, and the active-job guard that blocks a
/// delete while a job for the scope is still in flight.
/// </summary>
internal static class ScopeAudit
{
    /// <summary>Job statuses that count as "active" — a scope holding any of these must not be deleted.</summary>
    public static readonly JobStatus[] ActiveStatuses =
        [JobStatus.Draft, JobStatus.Queued, JobStatus.Running, JobStatus.Paused];

    /// <summary>Builds an append-only audit entry for a branch/instance CRUD action performed via the manager.</summary>
    public static AuditEntry Entry(string action, string? actor, string entityType, Guid entityId, string? detail) => new()
    {
        Id = Guid.NewGuid(),
        Actor = actor,
        Source = nameof(JobSource.Manager),
        Action = action,
        EntityType = entityType,
        EntityId = entityId.ToString(),
        Detail = detail,
    };

    /// <summary>True when the branch has at least one job in an active (Draft/Queued/Running/Paused) state.</summary>
    public static async Task<bool> HasActiveJobsAsync(IJobStore jobs, string branchKey, CancellationToken ct)
    {
        foreach (var status in ActiveStatuses)
        {
            var list = await jobs.ListAsync(new JobListQuery { BranchKey = branchKey, Status = status }, ct);
            if (list.Count > 0) return true;
        }
        return false;
    }
}
