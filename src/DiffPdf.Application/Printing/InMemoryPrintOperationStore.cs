using System.Collections.Concurrent;
using DiffPdf.Application.Abstractions;

namespace DiffPdf.Application.Printing;

/// <summary>
/// In-memory <see cref="IPrintOperationStore"/>. Observability only — the durable comparison job is the
/// source of truth, so losing a status to a restart is harmless (the comparison still runs and appears in
/// the Jobs list). <see cref="SetPhase"/> upserts so a status the handler updates after a restart is recreated.
/// </summary>
public sealed class InMemoryPrintOperationStore : IPrintOperationStore
{
    // User-triggered prints are low-volume; a simple unbounded map is fine. (Prune later if ever needed.)
    private readonly ConcurrentDictionary<Guid, PrintOperationStatus> _ops = new();

    public void Start(Guid operationId, string branchKey, string instanceKey) =>
        _ops[operationId] = new PrintOperationStatus(
            operationId, branchKey, instanceKey, PrintPhase.Printing,
            NewBatch: null, PreviousBatch: null, JobId: null,
            ValidationSummary: null, Error: null, UpdatedAt: DateTimeOffset.UtcNow);

    public void SetPhase(
        Guid operationId,
        PrintPhase phase,
        string? newBatch = null,
        string? previousBatch = null,
        Guid? jobId = null,
        string? validationSummary = null,
        string? error = null) =>
        _ops.AddOrUpdate(
            operationId,
            // Missing (status lost to a restart): seed from what this update carries.
            _ => new PrintOperationStatus(
                operationId, string.Empty, string.Empty, phase,
                newBatch, previousBatch, jobId, validationSummary, error, DateTimeOffset.UtcNow),
            // Present: advance the phase; non-null fields overwrite, nulls keep the prior value.
            (_, prev) => prev with
            {
                Phase = phase,
                NewBatch = newBatch ?? prev.NewBatch,
                PreviousBatch = previousBatch ?? prev.PreviousBatch,
                JobId = jobId ?? prev.JobId,
                ValidationSummary = validationSummary ?? prev.ValidationSummary,
                Error = error ?? prev.Error,
                UpdatedAt = DateTimeOffset.UtcNow,
            });

    public PrintOperationStatus? Get(Guid operationId) =>
        _ops.TryGetValue(operationId, out var status) ? status : null;
}
