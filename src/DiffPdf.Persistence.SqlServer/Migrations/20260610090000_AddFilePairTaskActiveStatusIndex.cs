using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DiffPdf.Persistence.SqlServer.Migrations
{
    /// <summary>
    /// Filtered index for the global active-task scans: <c>CountActiveAsync</c> (the backlog figure on
    /// <c>/health/ready</c> and <c>/api/v1/status</c>) and the stale-lease sweep both filter
    /// <c>file_pair_tasks</c> on <c>status</c> alone, which previously required a full table scan — every
    /// existing index on the table leads with <c>job_id</c>. The filtered index holds only Queued/Running
    /// rows, so it stays tiny no matter how much history accumulates; <c>locked_until</c> and <c>job_id</c>
    /// are included to cover the sweep and the terminal-job skip without key lookups.
    /// <para>
    /// Hand-written as guarded raw SQL on purpose: the index is operational tuning, intentionally NOT part
    /// of the EF model — the model snapshot is unchanged, so future <c>dotnet ef migrations add</c> output
    /// is unaffected. (No Designer file for the same reason; <c>MigrateAsync</c> only needs the attributes.)
    /// </para>
    /// </summary>
    [DbContext(typeof(DiffPdfDbContext))]
    [Migration("20260610090000_AddFilePairTaskActiveStatusIndex")]
    public partial class AddFilePairTaskActiveStatusIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                if not exists (select 1 from sys.indexes
                               where name = 'ix_file_pair_tasks_active_status'
                                 and object_id = object_id('file_pair_tasks'))
                    create nonclustered index ix_file_pair_tasks_active_status
                        on file_pair_tasks(status)
                        include (job_id, locked_until)
                        where status in ('Queued', 'Running');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("drop index if exists ix_file_pair_tasks_active_status on file_pair_tasks;");
        }
    }
}
