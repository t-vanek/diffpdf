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

    /// <summary>Max attempts for a single file pair before its transient failure is recorded as an error.</summary>
    public int MaxFilePairAttempts { get; set; } = 3;

    /// <summary>How often stale (expired-lease) file-pair tasks are recovered and re-dispatched.</summary>
    public int StaleRecoveryIntervalSeconds { get; set; } = 30;

    public TimeSpan StaleRecoveryInterval => TimeSpan.FromSeconds(StaleRecoveryIntervalSeconds);
}
