using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DiffPdf.Persistence.SqlServer.Migrations
{
    /// <summary>
    /// Baseline schema. <see cref="Up"/> runs the application's idempotent DDL (<c>if object_id(...) is
    /// null create table …</c>) rather than EF-generated <c>CreateTable</c> calls. This guarantees a fresh
    /// database gets the exact production schema — including the foreign keys, column defaults and the
    /// <c>automation_leader</c> table that are not part of the EF model — and lets databases already
    /// created by the previous hand-written migrator be adopted into EF migration history without
    /// failing on "object already exists". Subsequent schema changes use ordinary generated migrations.
    /// </summary>
    public partial class InitialCreate : Migration
    {
        private const string Schema = """
            if object_id(N'branches', N'U') is null
            create table branches (
                id uniqueidentifier not null primary key,
                [key] nvarchar(256) not null unique,
                name nvarchar(max) not null,
                enabled bit not null constraint df_branches_enabled default 1,
                created_at datetimeoffset not null constraint df_branches_created_at default sysdatetimeoffset(),
                updated_at datetimeoffset null,
                version bigint not null constraint df_branches_version default 1
            );

            if object_id(N'instances', N'U') is null
            create table instances (
                id uniqueidentifier not null primary key,
                branch_id uniqueidentifier not null references branches(id),
                [key] nvarchar(256) not null,
                name nvarchar(max) not null,
                base_path nvarchar(max) not null,
                credential_profile nvarchar(256) null,
                enabled bit not null constraint df_instances_enabled default 1,
                created_at datetimeoffset not null constraint df_instances_created_at default sysdatetimeoffset(),
                updated_at datetimeoffset null,
                version bigint not null constraint df_instances_version default 1,
                constraint uq_instances_branch_key unique (branch_id, [key])
            );

            if object_id(N'jobs', N'U') is null
            create table jobs (
                id uniqueidentifier not null primary key,
                branch_id uniqueidentifier not null references branches(id),
                instance_id uniqueidentifier not null references instances(id),
                status nvarchar(32) not null,
                created_at datetimeoffset not null constraint df_jobs_created_at default sysdatetimeoffset(),
                updated_at datetimeoffset null,
                started_at datetimeoffset null,
                completed_at datetimeoffset null,
                processed_count int not null constraint df_jobs_processed_count default 0,
                total_count int not null constraint df_jobs_total_count default 0,
                request_json nvarchar(max) not null,
                report_json nvarchar(max) null,
                error nvarchar(max) null,
                version bigint not null constraint df_jobs_version default 1,
                locked_by nvarchar(256) null,
                locked_until datetimeoffset null
            );

            if not exists (select 1 from sys.indexes where name = 'ix_jobs_branch_instance_created_at' and object_id = object_id(N'jobs'))
            create index ix_jobs_branch_instance_created_at on jobs (branch_id, instance_id, created_at desc);

            if not exists (select 1 from sys.indexes where name = 'ix_jobs_status_created_at' and object_id = object_id(N'jobs'))
            create index ix_jobs_status_created_at on jobs (status, created_at desc);

            if object_id(N'file_pair_tasks', N'U') is null
            create table file_pair_tasks (
                id uniqueidentifier not null primary key,
                job_id uniqueidentifier not null references jobs(id),
                relative_path nvarchar(max) not null,
                old_file_path nvarchar(max) null,
                new_file_path nvarchar(max) null,
                status nvarchar(32) not null,
                attempt_count int not null constraint df_file_pair_tasks_attempt_count default 0,
                error nvarchar(max) null,
                result_json nvarchar(max) null,
                created_at datetimeoffset not null constraint df_file_pair_tasks_created_at default sysdatetimeoffset(),
                started_at datetimeoffset null,
                completed_at datetimeoffset null,
                version bigint not null constraint df_file_pair_tasks_version default 1,
                locked_by nvarchar(256) null,
                locked_until datetimeoffset null
            );

            if not exists (select 1 from sys.indexes where name = 'ix_file_pair_tasks_job_status' and object_id = object_id(N'file_pair_tasks'))
            create index ix_file_pair_tasks_job_status on file_pair_tasks (job_id, status);

            if object_id(N'comparison_schedules', N'U') is null
            create table comparison_schedules (
                id uniqueidentifier not null primary key,
                branch_id uniqueidentifier not null references branches(id),
                instance_id uniqueidentifier not null references instances(id),
                branch_key nvarchar(256) not null,
                instance_key nvarchar(256) not null,
                [key] nvarchar(256) not null,
                name nvarchar(max) not null,
                cron nvarchar(256) not null,
                options_json nvarchar(max) not null,
                gate_json nvarchar(max) null,
                search_pattern nvarchar(256) not null constraint df_comparison_schedules_search_pattern default '*.pdf',
                recursive bit not null constraint df_comparison_schedules_recursive default 1,
                max_degree_of_parallelism int not null constraint df_comparison_schedules_mdop default 0,
                enabled bit not null constraint df_comparison_schedules_enabled default 1,
                created_at datetimeoffset not null constraint df_comparison_schedules_created_at default sysdatetimeoffset(),
                updated_at datetimeoffset null,
                last_run_at datetimeoffset null,
                version bigint not null constraint df_comparison_schedules_version default 1,
                constraint uq_comparison_schedules_instance_key unique (instance_id, [key])
            );

            if not exists (select 1 from sys.indexes where name = 'ix_comparison_schedules_enabled' and object_id = object_id(N'comparison_schedules'))
            create index ix_comparison_schedules_enabled on comparison_schedules (enabled);

            if object_id(N'notification_subscriptions', N'U') is null
            create table notification_subscriptions (
                id uniqueidentifier not null primary key,
                channel nvarchar(256) not null,
                target nvarchar(max) not null,
                events_json nvarchar(max) not null,
                branch_key nvarchar(256) null,
                instance_key nvarchar(256) null,
                enabled bit not null constraint df_notification_subscriptions_enabled default 1,
                created_at datetimeoffset not null constraint df_notification_subscriptions_created_at default sysdatetimeoffset(),
                updated_at datetimeoffset null,
                version bigint not null constraint df_notification_subscriptions_version default 1
            );

            if not exists (select 1 from sys.indexes where name = 'ix_notification_subscriptions_enabled' and object_id = object_id(N'notification_subscriptions'))
            create index ix_notification_subscriptions_enabled on notification_subscriptions (enabled);

            if object_id(N'automation_leader', N'U') is null
            create table automation_leader (
                role        nvarchar(64) not null primary key,
                owner       nvarchar(256) not null,
                acquired_at datetimeoffset not null constraint df_automation_leader_acquired default sysdatetimeoffset(),
                renewed_at  datetimeoffset not null constraint df_automation_leader_renewed default sysdatetimeoffset(),
                expires_at  datetimeoffset not null
            );

            if object_id(N'schedule_runs', N'U') is null
            create table schedule_runs (
                id uniqueidentifier not null primary key,
                schedule_id uniqueidentifier not null references comparison_schedules(id) on delete cascade,
                job_id uniqueidentifier not null unique,
                started_at datetimeoffset not null constraint df_schedule_runs_started default sysdatetimeoffset(),
                completed_at datetimeoffset null,
                outcome nvarchar(32) not null constraint df_schedule_runs_outcome default 'Pending',
                differing int not null constraint df_schedule_runs_differing default 0,
                errors int not null constraint df_schedule_runs_errors default 0,
                files_with_content_errors int not null constraint df_schedule_runs_fwce default 0,
                passed bit not null constraint df_schedule_runs_passed default 0,
                gate_violations_json nvarchar(max) null,
                error nvarchar(max) null
            );

            if not exists (select 1 from sys.indexes where name = 'ix_schedule_runs_schedule_started' and object_id = object_id(N'schedule_runs'))
            create index ix_schedule_runs_schedule_started on schedule_runs (schedule_id, started_at desc);

            if col_length(N'jobs', N'artifacts_pruned_at') is null
            alter table jobs add artifacts_pruned_at datetimeoffset null;

            if object_id(N'folder_watches', N'U') is null
            create table folder_watches (
                id uniqueidentifier not null primary key,
                branch_id uniqueidentifier not null references branches(id),
                instance_id uniqueidentifier not null unique references instances(id),
                branch_key nvarchar(256) not null,
                instance_key nvarchar(256) not null,
                stability_seconds int not null constraint df_folder_watches_stability default 30,
                enabled bit not null constraint df_folder_watches_enabled default 1,
                created_at datetimeoffset not null constraint df_folder_watches_created default sysdatetimeoffset(),
                updated_at datetimeoffset null,
                last_triggered_at datetimeoffset null,
                version bigint not null constraint df_folder_watches_version default 1
            );

            if not exists (select 1 from sys.indexes where name = 'ix_folder_watches_enabled' and object_id = object_id(N'folder_watches'))
            create index ix_folder_watches_enabled on folder_watches (enabled);
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(Schema);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                drop table if exists folder_watches;
                drop table if exists schedule_runs;
                drop table if exists automation_leader;
                drop table if exists notification_subscriptions;
                drop table if exists comparison_schedules;
                drop table if exists file_pair_tasks;
                drop table if exists jobs;
                drop table if exists instances;
                drop table if exists branches;
                """);
        }
    }
}
