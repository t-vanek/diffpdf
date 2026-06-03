using Npgsql;

namespace DiffPdf.Persistence.Postgres;

/// <summary>
/// Creates the application schema if it does not yet exist (idempotent). The EF
/// model maps to exactly these tables/columns; Wolverine manages its own tables.
/// </summary>
public static class PostgresMigrator
{
    private const string Schema = """
        create table if not exists branches (
            id uuid primary key,
            key text not null unique,
            name text not null,
            enabled boolean not null default true,
            created_at timestamptz not null default now(),
            updated_at timestamptz null,
            version bigint not null default 1
        );

        create table if not exists instances (
            id uuid primary key,
            branch_id uuid not null references branches(id),
            key text not null,
            name text not null,
            base_path text not null,
            credential_profile text null,
            enabled boolean not null default true,
            created_at timestamptz not null default now(),
            updated_at timestamptz null,
            version bigint not null default 1,
            constraint uq_instances_branch_key unique (branch_id, key)
        );

        create table if not exists jobs (
            id uuid primary key,
            branch_id uuid not null references branches(id),
            instance_id uuid not null references instances(id),
            status text not null,
            created_at timestamptz not null default now(),
            updated_at timestamptz null,
            started_at timestamptz null,
            completed_at timestamptz null,
            processed_count int not null default 0,
            total_count int not null default 0,
            request_json jsonb not null,
            report_json jsonb null,
            error text null,
            version bigint not null default 1,
            locked_by text null,
            locked_until timestamptz null
        );

        create index if not exists ix_jobs_branch_instance_created_at on jobs (branch_id, instance_id, created_at desc);
        create index if not exists ix_jobs_status_created_at on jobs (status, created_at desc);

        create table if not exists file_pair_tasks (
            id uuid primary key,
            job_id uuid not null references jobs(id),
            relative_path text not null,
            old_file_path text null,
            new_file_path text null,
            status text not null,
            attempt_count int not null default 0,
            error text null,
            result_json jsonb null,
            created_at timestamptz not null default now(),
            started_at timestamptz null,
            completed_at timestamptz null,
            version bigint not null default 1,
            locked_by text null,
            locked_until timestamptz null
        );

        create index if not exists ix_file_pair_tasks_job_status on file_pair_tasks (job_id, status);

        create table if not exists comparison_schedules (
            id uuid primary key,
            branch_id uuid not null references branches(id),
            instance_id uuid not null references instances(id),
            branch_key text not null,
            instance_key text not null,
            key text not null,
            name text not null,
            cron text not null,
            options_json jsonb not null,
            gate_json jsonb null,
            search_pattern text not null default '*.pdf',
            recursive boolean not null default true,
            max_degree_of_parallelism int not null default 0,
            enabled boolean not null default true,
            created_at timestamptz not null default now(),
            updated_at timestamptz null,
            last_run_at timestamptz null,
            version bigint not null default 1,
            constraint uq_comparison_schedules_instance_key unique (instance_id, key)
        );

        create index if not exists ix_comparison_schedules_enabled on comparison_schedules (enabled);

        create table if not exists notification_subscriptions (
            id uuid primary key,
            channel text not null,
            target text not null,
            events_json jsonb not null,
            branch_key text null,
            instance_key text null,
            enabled boolean not null default true,
            created_at timestamptz not null default now(),
            updated_at timestamptz null,
            version bigint not null default 1
        );

        create index if not exists ix_notification_subscriptions_enabled on notification_subscriptions (enabled);

        create table if not exists automation_leader (
            role        text primary key,
            owner       text not null,
            acquired_at timestamptz not null default now(),
            renewed_at  timestamptz not null default now(),
            expires_at  timestamptz not null
        );

        create table if not exists schedule_runs (
            id uuid primary key,
            schedule_id uuid not null references comparison_schedules(id) on delete cascade,
            job_id uuid not null unique,
            started_at timestamptz not null default now(),
            completed_at timestamptz null,
            outcome text not null default 'Pending',
            differing int not null default 0,
            errors int not null default 0,
            files_with_content_errors int not null default 0,
            passed boolean not null default false,
            gate_violations_json jsonb null,
            error text null
        );

        create index if not exists ix_schedule_runs_schedule_started on schedule_runs (schedule_id, started_at desc);

        alter table jobs add column if not exists artifacts_pruned_at timestamptz null;

        create table if not exists folder_watches (
            id uuid primary key,
            branch_id uuid not null references branches(id),
            instance_id uuid not null unique references instances(id),
            branch_key text not null,
            instance_key text not null,
            stability_seconds int not null default 30,
            enabled boolean not null default true,
            created_at timestamptz not null default now(),
            updated_at timestamptz null,
            last_triggered_at timestamptz null,
            version bigint not null default 1
        );

        create index if not exists ix_folder_watches_enabled on folder_watches (enabled);
        """;

    public static async Task MigrateAsync(string connectionString, CancellationToken ct = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(Schema, connection);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
