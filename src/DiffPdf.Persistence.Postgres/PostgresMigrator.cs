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
        """;

    public static async Task MigrateAsync(string connectionString, CancellationToken ct = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(Schema, connection);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
