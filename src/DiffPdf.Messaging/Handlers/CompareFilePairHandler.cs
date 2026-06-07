using DiffPdf.Core.Abstractions;
using DiffPdf.Core.Models;
using DiffPdf.Messaging.Messages;
using DiffPdf.Persistence;
using DiffPdf.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wolverine;
using Wolverine.Attributes;

namespace DiffPdf.Messaging.Handlers;

/// <summary>
/// Compares a single file pair, records the per-file result, advances the job's
/// processed counter, and triggers finalization when the last pair is done.
/// Always completes the task (recording errors as a result) so the batch can
/// never stall waiting on a pair.
/// </summary>
public sealed class CompareFilePairHandler
{
    // Wolverine caps every handler at DefaultExecutionTimeout (60s) by cancelling the token it hands us, and a
    // breach surfaces as a TaskCanceledException — indistinguishable here from a host shutdown. But a file-pair
    // comparison legitimately runs for minutes: the handler enforces its OWN cap (FilePairComparisonTimeout,
    // default 10 min) and records a per-file timeout error on expiry. Raise Wolverine's ceiling for this one
    // message well above that cap so the 60s default can never pre-empt the handler's own timeout — otherwise a
    // pair taking >60s is killed by Wolverine, misread as a shutdown, left Running, requeued by the stale-lease
    // sweeper, and killed again at 60s, looping forever so the job never finalizes.
    // INVARIANT: this must stay greater than WorkerOptions.FilePairComparisonTimeoutMinutes.
    [MessageTimeout(30 * 60)]
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
        // Check the job before claiming the task: if it is not Running (paused, cancelled
        // or finished) we leave the task untouched — a paused job's pending pairs stay
        // Queued so resume can re-dispatch them, while pairs already in flight finish.
        var job = await jobStore.GetAsync(command.JobId, ct);
        if (job is null || job.Status != JobStatus.Running)
            return;

        var task = await taskStore.TryClaimAsync(command.TaskId, workerInstance.WorkerInstanceId, workerOptions.Value.JobLease, ct);
        if (task is null)
            return; // already claimed / completed (idempotent)

        FilePairResult result;
        try
        {
            result = await CompareAsync(task, job, engine, paths, workerOptions.Value, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Host shutdown / message cancellation — NOT a pair failure. Don't record it as a per-file error
            // (CompareAsync deliberately propagates outer cancellation rather than turning it into one). Leave
            // the task Running so it is retried after restart (lease sweeper → redelivery), and let the
            // cancellation propagate so Wolverine treats the message as not-yet-processed.
            throw;
        }
        catch (Exception ex) when (ExceptionClassifier.IsTransient(ex) && task.AttemptCount < workerOptions.Value.MaxFilePairAttempts)
        {
            // Transient and still under the attempt cap: return the task to the queue and let Wolverine
            // redeliver (with cooldown). Requeue with None so a concurrent shutdown can't abandon the requeue
            // and strand the task in Running.
            logger.LogWarning(ex, "Transient failure on pair {Path} (attempt {Attempt}); requeuing", task.RelativePath, task.AttemptCount);
            await taskStore.RequeueAsync(task.Id, CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            // Permanent, or transient past the cap: record as an error and move on.
            logger.LogError(ex, "Pair {Path} failed permanently in job {JobId}", task.RelativePath, job.Id);
            result = new FilePairResult { RelativePath = task.RelativePath, Status = FilePairStatus.Error, Error = ex.Message };
        }

        // The comparison itself is finished — recording its result and advancing the batch must run to
        // completion even if ct is tripping (a host shutdown; the Wolverine message timeout is raised above the
        // per-pair cap via [MessageTimeout], so it never fires here). Tying these writes to the message token
        // would, on a shutdown mid-pair, abandon them: the task is left in Running (recovered only later by the
        // lease sweeper) and the finished — and now slow — comparison is thrown away and redone. So finalize
        // with CancellationToken.None, not ct.
        await taskStore.CompleteAsync(task.Id, result, FilePairTaskStatus.Completed, CancellationToken.None);

        var (processed, total) = await jobStore.IncrementProcessedAsync(command.JobId, CancellationToken.None);
        await progressPublisher.PublishAsync(new JobProgressChanged(
            job.Id, job.BranchKey, job.InstanceKey, "Running", processed, total,
            total == 0 ? 0 : (double)processed / total), CancellationToken.None);

        if (total > 0 && processed >= total)
            await bus.PublishAsync(new FinalizeBatch(job.Id));
    }

    internal static async Task<FilePairResult> CompareAsync(
        FilePairTask task, ComparisonJob job, IComparisonEngine engine,
        IJobStoragePathProvider paths, WorkerOptions options, CancellationToken ct)
    {
        if (task.OldFilePath is null)
            return new FilePairResult { RelativePath = task.RelativePath, Status = FilePairStatus.OnlyInNew };
        if (task.NewFilePath is null)
            return new FilePairResult { RelativePath = task.RelativePath, Status = FilePairStatus.OnlyInOld };

        // Pre-flight size guard: reject an oversized input before it is opened, so a pathologically large PDF
        // can't exhaust memory (the engine loads whole documents into RAM). 0 disables the check.
        if (options.MaxPdfSizeBytes > 0
            && (TooLarge(task.OldFilePath, options.MaxPdfSizeBytes) ?? TooLarge(task.NewFilePath, options.MaxPdfSizeBytes)) is { } sizeError)
            return new FilePairResult { RelativePath = task.RelativePath, Status = FilePairStatus.Error, Error = sizeError };

        // Unexpected exceptions propagate to the caller for transient/permanent classification.
        string artifacts = paths.GetArtifactsPath(job);

        // Hard wall-clock cap on the whole pair (probe + extract + every render + highlight). Run off the
        // message thread so even a synchronous hang (e.g. PdfPig opening a malformed file) is abandoned, and
        // cancel the engine cooperatively on expiry. Our timeout → a per-file error; outer cancellation
        // (a host shutdown — Wolverine's message timeout is raised above our cap) propagates untouched, so the
        // pair is left Running and recovered after restart.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(options.FilePairComparisonTimeout);
        var compare = Task.Run(
            () => engine.CompareAsync(task.OldFilePath, task.NewFilePath, job.Request.Options, artifacts, timeoutCts.Token),
            timeoutCts.Token);

        try
        {
            var fr = await compare.WaitAsync(timeoutCts.Token);

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
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            return new FilePairResult
            {
                RelativePath = task.RelativePath,
                Status = FilePairStatus.Error,
                Error = $"Comparison exceeded the {options.FilePairComparisonTimeout.TotalMinutes:0} min limit.",
            };
        }
    }

    /// <summary>Returns an error message if the file exists and exceeds <paramref name="maxBytes"/>, else null.</summary>
    private static string? TooLarge(string path, long maxBytes)
    {
        var info = new FileInfo(path);
        return info.Exists && info.Length > maxBytes
            ? $"PDF '{Path.GetFileName(path)}' is {info.Length / (1024 * 1024)} MB, over the {maxBytes / (1024 * 1024)} MB limit."
            : null;
    }
}
