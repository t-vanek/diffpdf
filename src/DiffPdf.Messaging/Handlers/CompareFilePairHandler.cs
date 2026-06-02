using DiffPdf.Core.Abstractions;
using DiffPdf.Core.Models;
using DiffPdf.Messaging.Messages;
using DiffPdf.Persistence;
using DiffPdf.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wolverine;

namespace DiffPdf.Messaging.Handlers;

/// <summary>
/// Compares a single file pair, records the per-file result, advances the job's
/// processed counter, and triggers finalization when the last pair is done.
/// Always completes the task (recording errors as a result) so the batch can
/// never stall waiting on a pair.
/// </summary>
public sealed class CompareFilePairHandler
{
    public static async Task Handle(
        CompareFilePair command,
        IJobStore jobStore,
        IFilePairTaskStore taskStore,
        IComparisonEngine engine,
        IJobStoragePathProvider paths,
        IJobProgressPublisher progressPublisher,
        IWorkerInstanceIdProvider workerInstance,
        IOptions<WorkerOptions> workerOptions,
        IMessageBus bus,
        ILogger<CompareFilePairHandler> logger,
        CancellationToken ct)
    {
        var task = await taskStore.TryClaimAsync(command.TaskId, workerInstance.WorkerInstanceId, workerOptions.Value.JobLease, ct);
        if (task is null)
            return; // already claimed / completed (idempotent)

        var job = await jobStore.GetAsync(command.JobId, ct);
        if (job is null) return;

        FilePairResult result;
        try
        {
            result = await CompareAsync(task, job, engine, paths, ct);
        }
        catch (Exception ex) when (ExceptionClassifier.IsTransient(ex) && task.AttemptCount < workerOptions.Value.MaxFilePairAttempts)
        {
            // Transient and still under the attempt cap: return the task to the queue
            // and let Wolverine redeliver (with cooldown) so it resumes on a retry.
            logger.LogWarning(ex, "Transient failure on pair {Path} (attempt {Attempt}); requeuing", task.RelativePath, task.AttemptCount);
            await taskStore.RequeueAsync(task.Id, ct);
            throw;
        }
        catch (Exception ex)
        {
            // Permanent, or transient past the cap: record as an error and move on.
            logger.LogError(ex, "Pair {Path} failed permanently in job {JobId}", task.RelativePath, job.Id);
            result = new FilePairResult { RelativePath = task.RelativePath, Status = FilePairStatus.Error, Error = ex.Message };
        }

        await taskStore.CompleteAsync(task.Id, result, FilePairTaskStatus.Completed, ct);

        var (processed, total) = await jobStore.IncrementProcessedAsync(command.JobId, ct);
        await progressPublisher.PublishAsync(new JobProgressChanged(
            job.Id, job.BusinessInstanceKey, job.ProjectKey, "Running", processed, total,
            total == 0 ? 0 : (double)processed / total), ct);

        if (total > 0 && processed >= total)
            await bus.PublishAsync(new FinalizeBatch(job.Id));
    }

    private static async Task<FilePairResult> CompareAsync(
        FilePairTask task, ComparisonJob job, IComparisonEngine engine,
        IJobStoragePathProvider paths, CancellationToken ct)
    {
        if (task.OldFilePath is null)
            return new FilePairResult { RelativePath = task.RelativePath, Status = FilePairStatus.OnlyInNew };
        if (task.NewFilePath is null)
            return new FilePairResult { RelativePath = task.RelativePath, Status = FilePairStatus.OnlyInOld };

        // Unexpected exceptions propagate to the caller for transient/permanent classification.
        string artifacts = paths.GetArtifactsPath(job);
        var fr = await engine.CompareAsync(task.OldFilePath, task.NewFilePath, job.Request.Options, artifacts, ct);

        // A readable-but-broken PDF is a handled (permanent) outcome, not a thrown error.
        if (fr.Outcome == ComparisonOutcome.Failed)
            return new FilePairResult { RelativePath = task.RelativePath, Status = FilePairStatus.Error, Error = fr.Error };

        return new FilePairResult
        {
            RelativePath = task.RelativePath,
            Status = fr.AreIdentical ? FilePairStatus.Identical : FilePairStatus.Differs,
            Similarity = fr.Similarity,
            DifferingPages = fr.DifferingPages,
            ContentErrorCount = fr.ContentErrors.Count,
            HighlightedPdfPath = fr.HighlightedPdfPath,
        };
    }
}
