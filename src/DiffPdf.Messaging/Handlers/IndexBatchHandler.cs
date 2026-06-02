using DiffPdf.Core.Abstractions;
using DiffPdf.Core.Comparison;
using DiffPdf.Core.Models;
using DiffPdf.Messaging.Messages;
using DiffPdf.Persistence;
using Microsoft.Extensions.Logging;
using Wolverine;

namespace DiffPdf.Messaging.Handlers;

/// <summary>Pairs the job's folders into file-pair tasks, sets the total, and dispatches a task each.</summary>
public sealed class IndexBatchHandler
{
    public static async Task Handle(
        IndexBatch command,
        IJobStore jobStore,
        IFilePairTaskStore taskStore,
        IStorageProvisioner provisioner,
        IMessageBus bus,
        ILogger<IndexBatchHandler> logger,
        CancellationToken ct)
    {
        var job = await jobStore.GetAsync(command.JobId, ct);
        if (job is null || job.Status != JobStatus.Running)
        {
            logger.LogInformation("IndexBatch skipped for {JobId} (status {Status}).", command.JobId, job?.Status);
            return;
        }

        await provisioner.EnsureJobFoldersAsync(job, ct);

        IReadOnlyList<FilePair> pairs;
        try
        {
            var req = job.Request;
            pairs = FolderPairing.Pair(req.OldFolder, req.NewFolder, req.SearchPattern, req.Recursive);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Indexing failed for job {JobId}", job.Id);
            await jobStore.FailAsync(job.Id, ex.Message, job.Version, ct);
            return;
        }

        await jobStore.SetTotalAsync(job.Id, pairs.Count, ct);

        if (pairs.Count == 0)
        {
            await bus.PublishAsync(new FinalizeBatch(job.Id));
            return;
        }

        var tasks = pairs.Select(p => new FilePairTask
        {
            Id = Guid.NewGuid(),
            JobId = job.Id,
            RelativePath = p.RelativePath,
            OldFilePath = p.OldPath,
            NewFilePath = p.NewPath,
        }).ToList();

        await taskStore.CreateManyAsync(tasks, ct);

        foreach (var task in tasks)
            await bus.PublishAsync(new CompareFilePair(job.Id, task.Id));

        logger.LogInformation("Job {JobId} indexed into {Count} file-pair tasks.", job.Id, tasks.Count);
    }
}
