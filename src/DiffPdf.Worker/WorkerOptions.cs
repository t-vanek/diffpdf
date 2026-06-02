namespace DiffPdf.Worker;

public sealed class WorkerOptions
{
    /// <summary>Max batch jobs processed concurrently by one worker (RabbitMQ listener count).</summary>
    public int MaxConcurrentJobs { get; set; } = 2;

    /// <summary>Max file pairs compared in parallel within a single job.</summary>
    public int MaxFilePairsPerJob { get; set; } = 2;

    /// <summary>Global cap on concurrent (CPU/RAM-heavy) PDF render operations across all jobs.</summary>
    public int MaxConcurrentPdfOperations { get; set; } = 4;

    /// <summary>How long a claimed job's lease is held before it is considered stale.</summary>
    public int JobLockMinutes { get; set; } = 5;

    public TimeSpan JobLease => TimeSpan.FromMinutes(JobLockMinutes);
}
