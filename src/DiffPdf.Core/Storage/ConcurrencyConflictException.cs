namespace DiffPdf.Core.Storage;

/// <summary>Thrown when an optimistic-concurrency guarded update finds a stale version / unexpected state.</summary>
public sealed class ConcurrencyConflictException(string message) : Exception(message);
