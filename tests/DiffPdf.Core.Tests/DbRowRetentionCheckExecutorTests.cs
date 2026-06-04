using DiffPdf.Core.Models;
using DiffPdf.Messaging.ControlPlane;
using DiffPdf.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiffPdf.Core.Tests;

/// <summary>
/// Part 4 DB-row retention: finished jobs (+ their tasks) whose artifacts are already pruned, and trigger-run
/// and audit history, are deleted once older than the window — while fresh rows (and jobs whose artifacts are
/// not yet pruned) are kept, so row deletion can never orphan on-disk reports.
/// </summary>
public class DbRowRetentionCheckExecutorTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static ComparisonJob CompletedJob(DateTimeOffset completedAt, bool artifactsPruned) => new()
    {
        Id = Guid.NewGuid(),
        BranchId = Guid.NewGuid(),
        InstanceId = Guid.NewGuid(),
        Status = JobStatus.Completed,
        CompletedAt = completedAt,
        ArtifactsPrunedAt = artifactsPruned ? completedAt : null,
        Request = new BatchComparisonRequest
        {
            Scope = new JobScope("Alfa", "Lama"),
            OldFolder = "/old", NewFolder = "/new", ReportsFolder = "/reports",
        },
    };

    private static ControlCheck Check(int retentionDays) => new()
    {
        Id = Guid.NewGuid(), Key = "db-row-retention", Name = "DB retention",
        Type = CheckType.DbRowRetention, ScopeKind = CheckScopeKind.Global,
        Parameters = new Dictionary<string, string> { ["retentionDays"] = retentionDays.ToString() },
    };

    [Fact]
    public async Task Prunes_old_rows_and_keeps_fresh_ones()
    {
        var jobs = new InMemoryJobStore();
        var tasks = new InMemoryFilePairTaskStore();
        var runs = new InMemoryTriggerRunStore();
        var audit = new InMemoryAuditLogStore();
        var triggerId = Guid.NewGuid();

        // Old + artifacts pruned → eligible.
        var oldJob = await jobs.CreateAsync(CompletedJob(Now.AddDays(-200), artifactsPruned: true));
        await tasks.CreateManyAsync([new FilePairTask { Id = Guid.NewGuid(), JobId = oldJob.Id, RelativePath = "a.pdf" }]);
        // Old but artifacts NOT pruned → kept (guard against orphaning on-disk reports).
        var oldUnpruned = await jobs.CreateAsync(CompletedJob(Now.AddDays(-200), artifactsPruned: false));
        // Fresh → kept.
        var freshJob = await jobs.CreateAsync(CompletedJob(Now.AddDays(-10), artifactsPruned: true));
        await tasks.CreateManyAsync([new FilePairTask { Id = Guid.NewGuid(), JobId = freshJob.Id, RelativePath = "b.pdf" }]);

        await runs.AddAsync(new TriggerRun { Id = Guid.NewGuid(), TriggerId = triggerId, StartedAt = Now.AddDays(-200) });
        await runs.AddAsync(new TriggerRun { Id = Guid.NewGuid(), TriggerId = triggerId, StartedAt = Now.AddDays(-10) });

        await audit.AddAsync(new AuditEntry { Id = Guid.NewGuid(), At = Now.AddDays(-200), Action = "old", EntityType = "Job" });
        await audit.AddAsync(new AuditEntry { Id = Guid.NewGuid(), At = Now.AddDays(-10), Action = "fresh", EntityType = "Job" });

        var executor = new DbRowRetentionCheckExecutor(jobs, tasks, runs, audit, NullLogger<DbRowRetentionCheckExecutor>.Instance);
        var result = await executor.ExecuteAsync(Check(90), CancellationToken.None);

        Assert.Equal(CheckRunOutcome.Ok, result.Outcome);

        Assert.Null(await jobs.GetAsync(oldJob.Id));               // old pruned-artifact job gone
        Assert.Empty(await tasks.ListByJobAsync(oldJob.Id));       // its tasks gone
        Assert.NotNull(await jobs.GetAsync(oldUnpruned.Id));       // artifacts-not-pruned job kept
        Assert.NotNull(await jobs.GetAsync(freshJob.Id));          // fresh job kept
        Assert.Single(await tasks.ListByJobAsync(freshJob.Id));    // its tasks kept

        var remainingRuns = await runs.ListByTriggerAsync(triggerId);
        Assert.Single(remainingRuns);
        Assert.True(remainingRuns[0].StartedAt > Now.AddDays(-90));

        var remainingAudit = await audit.ListAsync();
        Assert.Single(remainingAudit);
        Assert.True(remainingAudit[0].At > Now.AddDays(-90));
    }

    [Fact]
    public async Task Nothing_to_prune_returns_ok()
    {
        var executor = new DbRowRetentionCheckExecutor(
            new InMemoryJobStore(), new InMemoryFilePairTaskStore(), new InMemoryTriggerRunStore(),
            new InMemoryAuditLogStore(), NullLogger<DbRowRetentionCheckExecutor>.Instance);

        var result = await executor.ExecuteAsync(Check(90), CancellationToken.None);

        Assert.Equal(CheckRunOutcome.Ok, result.Outcome);
        Assert.Contains("nothing to prune", result.Detail);
    }
}
