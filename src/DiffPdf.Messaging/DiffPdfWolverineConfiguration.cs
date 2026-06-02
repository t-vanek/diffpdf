using DiffPdf.Messaging.Handlers;
using DiffPdf.Messaging.Messages;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.ErrorHandling;
using Wolverine.Postgresql;
using Wolverine.RabbitMQ;
using Wolverine.SqlServer;

namespace DiffPdf.Messaging;

/// <summary>Relational database backing Wolverine's durable inbox/outbox.</summary>
public enum DiffPdfDatabase
{
    Postgres,
    SqlServer,
}

public static class DiffPdfWolverineConfiguration
{
    public const string BatchQueue = "diffpdf.batch.commands";

    /// <summary>
    /// Wires Wolverine for diffpdf: RabbitMQ transport for the batch command,
    /// a relational durable inbox/outbox (PostgreSQL or SQL Server), and handler
    /// discovery for this assembly.
    /// </summary>
    public static void ConfigureDiffPdfMessaging(
        this WolverineOptions opts,
        string rabbitConnectionString,
        string databaseConnectionString,
        DiffPdfDatabase database = DiffPdfDatabase.Postgres,
        int listenerCount = 2,
        int preFetchCount = 4)
    {
        opts.UseRuntimeCompilation();
        opts.Discovery.IncludeAssembly(typeof(RunBatchComparisonHandler).Assembly);

        if (database == DiffPdfDatabase.SqlServer)
            opts.PersistMessagesWithSqlServer(databaseConnectionString);
        else
            opts.PersistMessagesWithPostgresql(databaseConnectionString);
        opts.UseEntityFrameworkCoreTransactions();

        // Transient failures (IO / network / broker blips) are retried with
        // cooldown, then dead-lettered. Permanent failures are recorded by the
        // handler and acknowledged, so they never bubble to this policy.
        opts.Policies.OnException(ExceptionClassifier.IsTransient)
            .RetryWithCooldown(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10));

        opts.UseRabbitMq(new Uri(rabbitConnectionString))
            .AutoProvision();

        opts.PublishMessage<RunBatchComparison>()
            .ToRabbitQueue(BatchQueue)
            .UseDurableOutbox();

        opts.ListenToRabbitQueue(BatchQueue)
            .PreFetchCount((ushort)preFetchCount)
            .ListenerCount(listenerCount)
            .UseDurableInbox();
    }
}
