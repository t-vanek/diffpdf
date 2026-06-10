using DiffPdf.Messaging.Handlers;
using DiffPdf.Messaging.Messages;
using DiffPdf.Worker;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.ErrorHandling;
using Wolverine.SqlServer;

namespace DiffPdf.Messaging;

public static class DiffPdfWolverineConfiguration
{
    /// <summary>
    /// Wires Wolverine for diffpdf <b>without any external broker</b>: commands flow through
    /// DB-backed <b>durable local queues</b> (SQL Server), with a transactional inbox/outbox
    /// and retry. Messages are persisted, so they survive a restart and are processed
    /// in-process by Wolverine's durable agents — no RabbitMQ required.
    /// </summary>
    public static void ConfigureDiffPdfMessaging(
        this WolverineOptions opts,
        string databaseConnectionString,
        WorkerOptions? worker = null)
    {
        worker ??= new WorkerOptions();

        opts.UseRuntimeCompilation();
        opts.Discovery.IncludeAssembly(typeof(RunBatchComparisonHandler).Assembly);

        opts.PersistMessagesWithSqlServer(databaseConnectionString);
        opts.UseEntityFrameworkCoreTransactions();

        // Transient failures (IO / network blips) are retried with cooldown, then dead-lettered.
        // Permanent failures are recorded by the handler and acknowledged, so they never bubble here.
        opts.Policies.OnException(ExceptionClassifier.IsTransient)
            .RetryWithCooldown(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10));

        // CompareFilePair is the CPU/RAM-heavy stream — pin its in-process parallelism to the configured
        // worker budget instead of inheriting Wolverine's local-queue default. Renders inside each pair are
        // further capped by IPdfWorkLimiter (Pdf:MaxConcurrentOperations), which only covers the render slice.
        opts.LocalQueueFor<CompareFilePair>()
            .MaximumParallelMessages(Math.Max(1, worker.MaxConcurrentJobs * worker.MaxFilePairsPerJob));

        // No external broker: every command (RunBatchComparison, IndexBatch, CompareFilePair,
        // FinalizeBatch, …) is routed to a durable local queue persisted in the relational store,
        // giving the same at-least-once + survives-restart guarantees RabbitMQ provided, single-node.
        opts.Policies.UseDurableLocalQueues();
    }
}
