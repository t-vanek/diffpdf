using Npgsql;

namespace DiffPdf.Persistence.Postgres;

/// <summary>Creates the schema if it does not yet exist. Idempotent.</summary>
public static class PostgresMigrator
{
    private const string Schema = """
        create table if not exists business_instances (
            id uuid primary key,
            key text not null unique,
            name text not null,
            enabled boolean not null default true,
            created_at timestamptz not null default now(),
            updated_at timestamptz null,
            version bigint not null default 1,
            constraint ck_business_instances_key_not_empty check (length(trim(key)) > 0)
        );

        create table if not exists projects (
            id uuid primary key,
            business_instance_id uuid not null references business_instances(id),
            key text not null,
            name text not null,
            enabled boolean not null default true,
            created_at timestamptz not null default now(),
            updated_at timestamptz null,
            version bigint not null default 1,
            constraint uq_projects_business_instance_key unique (business_instance_id, key),
            constraint ck_projects_key_not_empty check (length(trim(key)) > 0)
        );

        create table if not exists jobs (
            id uuid primary key,
            business_instance_id uuid not null references business_instances(id),
            project_id uuid not null references projects(id),
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
            locked_until timestamptz null,
            constraint ck_jobs_status check (status in ('Queued','Running','Completed','Failed','Cancelled')),
            constraint ck_jobs_processed_count_nonnegative check (processed_count >= 0),
            constraint ck_jobs_total_count_nonnegative check (total_count >= 0)
        );

        create index if not exists ix_jobs_business_project_created_at on jobs (business_instance_id, project_id, created_at desc);
        create index if not exists ix_jobs_status_created_at on jobs (status, created_at desc);
        create index if not exists ix_jobs_locked_until on jobs (locked_until);
        """;

    public static async Task MigrateAsync(NpgsqlDataSource dataSource, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(Schema);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
