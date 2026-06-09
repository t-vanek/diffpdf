namespace DiffPdf.Worker;

public sealed class WorkerOptions
{
    /// <summary>Max batch jobs processed concurrently by one worker.</summary>
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

    /// <summary>
    /// Hard wall-clock cap on comparing a single file pair (probe + extract + all page renders + highlight).
    /// On expiry the pair is recorded as an error and the batch moves on — a corrupt/huge input can never
    /// wedge a worker indefinitely. Distinct from <see cref="JobLease"/> (which governs stale-job recovery).
    /// </summary>
    public int FilePairComparisonTimeoutMinutes { get; set; } = 10;

    public TimeSpan FilePairComparisonTimeout => TimeSpan.FromMinutes(FilePairComparisonTimeoutMinutes);

    /// <summary>
    /// Lease held while comparing a single file pair. MUST exceed <see cref="FilePairComparisonTimeout"/> so the
    /// stale-lease sweeper cannot reclaim a pair that is still legitimately comparing — otherwise a long-but-healthy
    /// pair is requeued mid-flight and a second worker runs it again (wasted compute + a double-counted batch). The
    /// +2 min slack sits comfortably above <see cref="StaleRecoveryInterval"/>, so the sweeper can never fire during
    /// a legal run. Distinct from <see cref="JobLease"/>, which governs the job-level (indexing) lease.
    /// </summary>
    public TimeSpan FilePairLease => FilePairComparisonTimeout + TimeSpan.FromMinutes(2);

    /// <summary>
    /// On startup, return ALL <c>Running</c> file-pair tasks to the queue and re-dispatch them — they are orphans
    /// left mid-comparison by a previous process that crashed without releasing them, and reclaiming them at
    /// startup recovers the batch in seconds instead of waiting out the ~12-min <see cref="FilePairLease"/>.
    /// <para>
    /// <b>Single-instance only.</b> With multiple worker replicas, a booting node would reclaim pairs its peers
    /// are actively comparing → those pairs run twice (the complete-once guard caps the cost at wasted compute,
    /// never a corrupted count, but it is still waste). Set <c>false</c> for a multi-replica deployment, where the
    /// per-replica graceful-shutdown release plus the stale-lease sweeper cover crash recovery.
    /// </para>
    /// </summary>
    public bool ReclaimOrphansOnStartup { get; set; } = true;

    /// <summary>
    /// Maximum size of an input PDF (bytes). A file larger than this is rejected as an error before it is
    /// opened, so a pathologically large document can't exhaust memory. Default 500 MB; 0 disables the check.
    /// </summary>
    public long MaxPdfSizeBytes { get; set; } = 500L * 1024 * 1024;
}
