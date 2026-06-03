using DiffPdf.Messaging.Handlers;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.ErrorHandling;
using Wolverine.Postgresql;
using Wolverine.SqlServer;

namespace DiffPdf.Messaging;

/// <summary>Relational database backing Wolverine's durable inbox/outbox + local queues.</summary>
public enum DiffPdfDatabase
{
    Postgres,
    SqlServer,
}

public static class DiffPdfWolverineConfiguration
{
    /// <summary>
    /// Wires Wolverine for diffpdf <b>without any external broker</b>: commands flow through
    /// DB-backed <b>durable local queues</b> (PostgreSQL or SQL Server), with a transactional
    /// inbox/outbox and retry. Messages are persisted, so they survive a restart and are
    /// processed in-process by Wolverine's durable agents — no RabbitMQ required.
    /// </summary>
    public static void ConfigureDiffPdfMessaging(
        this WolverineOptions opts,
        string databaseConnectionString,
        DiffPdfDatabase database = DiffPdfDatabase.Postgres)
    {
        opts.UseRuntimeCompilation();
        opts.Discovery.IncludeAssembly(typeof(RunBatchComparisonHandler).Assembly);

        if (database == DiffPdfDatabase.SqlServer)
            opts.PersistMessagesWithSqlServer(databaseConnectionString);
        else
            opts.PersistMessagesWithPostgresql(databaseConnectionString);
        opts.UseEntityFrameworkCoreTransactions();

        // Transient failures (IO / network blips) are retried with cooldown, then dead-lettered.
        // Permanent failures are recorded by the handler and acknowledged, so they never bubble here.
        opts.Policies.OnException(ExceptionClassifier.IsTransient)
            .RetryWithCooldown(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10));

        // No external broker: every command (RunBatchComparison, IndexBatch, CompareFilePair,
        // FinalizeBatch, …) is routed to a durable local queue persisted in the relational store,
        // giving the same at-least-once + survives-restart guarantees RabbitMQ provided, single-node.
        opts.Policies.UseDurableLocalQueues();
    }
}
