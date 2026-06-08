namespace DiffPdf.Application.Jobs;

/// <summary>
/// A job lifecycle action attempted from a state that does not allow it (e.g. pausing a non-running job,
/// retrying an unfinished one). Maps to 409 at the endpoint. Job-not-found is signalled by a null return instead.
/// </summary>
public sealed class JobConflictException(string message) : Exception(message);
