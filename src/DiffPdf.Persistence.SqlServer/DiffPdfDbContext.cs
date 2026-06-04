using DiffPdf.Persistence.SqlServer.Entities;
using Microsoft.EntityFrameworkCore;

namespace DiffPdf.Persistence.SqlServer;

public sealed class DiffPdfDbContext(DbContextOptions<DiffPdfDbContext> options) : DbContext(options)
{
    public DbSet<BranchEntity> Branches => Set<BranchEntity>();
    public DbSet<InstanceEntity> Instances => Set<InstanceEntity>();
    public DbSet<JobEntity> Jobs => Set<JobEntity>();
    public DbSet<FilePairTaskEntity> FilePairTasks => Set<FilePairTaskEntity>();
    public DbSet<SubscriptionEntity> Subscriptions => Set<SubscriptionEntity>();
    public DbSet<ControlCheckEntity> ControlChecks => Set<ControlCheckEntity>();
    public DbSet<ControlCheckRunEntity> ControlCheckRuns => Set<ControlCheckRunEntity>();
    public DbSet<TriggerEntity> Triggers => Set<TriggerEntity>();
    public DbSet<TriggerRunEntity> TriggerRuns => Set<TriggerRunEntity>();
    public DbSet<AuditLogEntity> AuditLog => Set<AuditLogEntity>();
    public DbSet<ScopeConfigurationEntity> ScopeConfigurations => Set<ScopeConfigurationEntity>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<BranchEntity>(e =>
        {
            e.ToTable("branches");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Key).HasColumnName("key").HasMaxLength(256);
            e.Property(x => x.Name).HasColumnName("name");
            e.Property(x => x.Enabled).HasColumnName("enabled");
            e.Property(x => x.QueuePaused).HasColumnName("queue_paused");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.Property(x => x.Version).HasColumnName("version");
            e.HasIndex(x => x.Key).IsUnique();
        });

        b.Entity<InstanceEntity>(e =>
        {
            e.ToTable("instances");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.BranchId).HasColumnName("branch_id");
            e.Property(x => x.Key).HasColumnName("key").HasMaxLength(256);
            e.Property(x => x.Name).HasColumnName("name");
            e.Property(x => x.BasePath).HasColumnName("base_path");
            e.Property(x => x.CredentialProfile).HasColumnName("credential_profile");
            e.Property(x => x.Enabled).HasColumnName("enabled");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.Property(x => x.Version).HasColumnName("version");
            e.HasIndex(x => new { x.BranchId, x.Key }).IsUnique();
        });

        b.Entity<JobEntity>(e =>
        {
            e.ToTable("jobs");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.BranchId).HasColumnName("branch_id");
            e.Property(x => x.InstanceId).HasColumnName("instance_id");
            e.Property(x => x.Status).HasColumnName("status").HasMaxLength(32);
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.Property(x => x.StartedAt).HasColumnName("started_at");
            e.Property(x => x.CompletedAt).HasColumnName("completed_at");
            e.Property(x => x.ProcessedCount).HasColumnName("processed_count");
            e.Property(x => x.TotalCount).HasColumnName("total_count");
            e.Property(x => x.RequestJson).HasColumnName("request_json");
            e.Property(x => x.ReportJson).HasColumnName("report_json");
            e.Property(x => x.Error).HasColumnName("error");
            e.Property(x => x.Version).HasColumnName("version");
            e.Property(x => x.LockedBy).HasColumnName("locked_by").HasMaxLength(256);
            e.Property(x => x.LockedUntil).HasColumnName("locked_until");
            e.Property(x => x.ArtifactsPrunedAt).HasColumnName("artifacts_pruned_at");
            e.Property(x => x.TriggerId).HasColumnName("trigger_id");
            e.Property(x => x.Source).HasColumnName("source").HasMaxLength(32);
            e.Property(x => x.Priority).HasColumnName("priority");
            e.HasIndex(x => new { x.BranchId, x.InstanceId, x.CreatedAt });
            e.HasIndex(x => new { x.Status, x.CreatedAt });
            e.HasIndex(x => new { x.BranchId, x.Status });
            e.HasIndex(x => x.TriggerId);
        });

        b.Entity<FilePairTaskEntity>(e =>
        {
            e.ToTable("file_pair_tasks");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.JobId).HasColumnName("job_id");
            e.Property(x => x.RelativePath).HasColumnName("relative_path");
            e.Property(x => x.OldFilePath).HasColumnName("old_file_path");
            e.Property(x => x.NewFilePath).HasColumnName("new_file_path");
            e.Property(x => x.Status).HasColumnName("status").HasMaxLength(32);
            e.Property(x => x.AttemptCount).HasColumnName("attempt_count");
            e.Property(x => x.Error).HasColumnName("error");
            e.Property(x => x.ResultJson).HasColumnName("result_json");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.StartedAt).HasColumnName("started_at");
            e.Property(x => x.CompletedAt).HasColumnName("completed_at");
            e.Property(x => x.Version).HasColumnName("version");
            e.Property(x => x.LockedBy).HasColumnName("locked_by").HasMaxLength(256);
            e.Property(x => x.LockedUntil).HasColumnName("locked_until");
            e.HasIndex(x => new { x.JobId, x.Status });
        });

        b.Entity<SubscriptionEntity>(e =>
        {
            e.ToTable("notification_subscriptions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Channel).HasColumnName("channel").HasMaxLength(256);
            e.Property(x => x.Target).HasColumnName("target");
            e.Property(x => x.EventsJson).HasColumnName("events_json");
            e.Property(x => x.BranchKey).HasColumnName("branch_key").HasMaxLength(256);
            e.Property(x => x.InstanceKey).HasColumnName("instance_key").HasMaxLength(256);
            e.Property(x => x.Enabled).HasColumnName("enabled");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.Property(x => x.Version).HasColumnName("version");
            e.HasIndex(x => x.Enabled);
        });

        b.Entity<ControlCheckEntity>(e =>
        {
            e.ToTable("control_checks");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Key).HasColumnName("key").HasMaxLength(256);
            e.Property(x => x.Name).HasColumnName("name");
            e.Property(x => x.Type).HasColumnName("type").HasMaxLength(32);
            e.Property(x => x.ScopeKind).HasColumnName("scope_kind").HasMaxLength(32);
            e.Property(x => x.BranchKey).HasColumnName("branch_key").HasMaxLength(256);
            e.Property(x => x.InstanceKey).HasColumnName("instance_key").HasMaxLength(256);
            e.Property(x => x.Cron).HasColumnName("cron").HasMaxLength(256);
            e.Property(x => x.IntervalSeconds).HasColumnName("interval_seconds");
            e.Property(x => x.ParametersJson).HasColumnName("parameters_json");
            e.Property(x => x.EventsJson).HasColumnName("events_json");
            e.Property(x => x.Enabled).HasColumnName("enabled");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.Property(x => x.LastRunAt).HasColumnName("last_run_at");
            e.Property(x => x.LastOutcome).HasColumnName("last_outcome").HasMaxLength(32);
            e.Property(x => x.Version).HasColumnName("version");
            e.HasIndex(x => x.Key).IsUnique();
            e.HasIndex(x => x.Enabled);
        });

        b.Entity<ControlCheckRunEntity>(e =>
        {
            e.ToTable("control_check_runs");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.CheckId).HasColumnName("check_id");
            e.Property(x => x.StartedAt).HasColumnName("started_at");
            e.Property(x => x.CompletedAt).HasColumnName("completed_at");
            e.Property(x => x.Outcome).HasColumnName("outcome").HasMaxLength(32);
            e.Property(x => x.Detail).HasColumnName("detail");
            e.HasIndex(x => new { x.CheckId, x.StartedAt });
        });

        b.Entity<TriggerEntity>(e =>
        {
            e.ToTable("triggers");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.BranchId).HasColumnName("branch_id");
            e.Property(x => x.InstanceId).HasColumnName("instance_id");
            e.Property(x => x.BranchKey).HasColumnName("branch_key").HasMaxLength(256);
            e.Property(x => x.InstanceKey).HasColumnName("instance_key").HasMaxLength(256);
            e.Property(x => x.Name).HasColumnName("name");
            e.Property(x => x.Description).HasColumnName("description");
            e.Property(x => x.ActionType).HasColumnName("action_type").HasMaxLength(32);
            e.Property(x => x.Status).HasColumnName("status").HasMaxLength(32);
            e.Property(x => x.Enabled).HasColumnName("enabled");
            e.Property(x => x.IsDefault).HasColumnName("is_default");
            e.Property(x => x.SpecJson).HasColumnName("spec_json");
            e.Property(x => x.RunCount).HasColumnName("run_count");
            e.Property(x => x.LastRunAt).HasColumnName("last_run_at");
            e.Property(x => x.LastOutcome).HasColumnName("last_outcome").HasMaxLength(64);
            e.Property(x => x.CreatedBy).HasColumnName("created_by").HasMaxLength(256);
            e.Property(x => x.UpdatedBy).HasColumnName("updated_by").HasMaxLength(256);
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.Property(x => x.IsDeleted).HasColumnName("is_deleted");
            e.Property(x => x.DeletedAt).HasColumnName("deleted_at");
            e.Property(x => x.Version).HasColumnName("version");
            e.HasIndex(x => new { x.InstanceId, x.Status });
            e.HasIndex(x => x.BranchId);
            // At most one non-deleted default trigger per instance.
            e.HasIndex(x => x.InstanceId).IsUnique().HasFilter("[is_default] = 1 AND [is_deleted] = 0");
        });

        b.Entity<TriggerRunEntity>(e =>
        {
            e.ToTable("trigger_runs");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TriggerId).HasColumnName("trigger_id");
            e.Property(x => x.BatchJobId).HasColumnName("batch_job_id");
            e.Property(x => x.Source).HasColumnName("source").HasMaxLength(32);
            e.Property(x => x.Status).HasColumnName("status").HasMaxLength(32);
            e.Property(x => x.Result).HasColumnName("result");
            e.Property(x => x.StartedAt).HasColumnName("started_at");
            e.Property(x => x.FinishedAt).HasColumnName("finished_at");
            e.Property(x => x.DurationMs).HasColumnName("duration_ms");
            e.Property(x => x.Error).HasColumnName("error");
            e.Property(x => x.RequestedBy).HasColumnName("requested_by").HasMaxLength(256);
            e.Property(x => x.IdempotencyKey).HasColumnName("idempotency_key").HasMaxLength(256);
            e.HasIndex(x => new { x.TriggerId, x.StartedAt });
            e.HasIndex(x => new { x.TriggerId, x.IdempotencyKey }).IsUnique().HasFilter("[idempotency_key] IS NOT NULL");
        });

        b.Entity<AuditLogEntity>(e =>
        {
            e.ToTable("audit_log");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.At).HasColumnName("at");
            e.Property(x => x.Actor).HasColumnName("actor").HasMaxLength(256);
            e.Property(x => x.Source).HasColumnName("source").HasMaxLength(32);
            e.Property(x => x.Action).HasColumnName("action").HasMaxLength(64);
            e.Property(x => x.EntityType).HasColumnName("entity_type").HasMaxLength(64);
            e.Property(x => x.EntityId).HasColumnName("entity_id").HasMaxLength(256);
            e.Property(x => x.Detail).HasColumnName("detail");
            e.HasIndex(x => new { x.EntityType, x.EntityId, x.At });
            e.HasIndex(x => x.At);
        });

        b.Entity<ScopeConfigurationEntity>(e =>
        {
            e.ToTable("scope_configurations");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Level).HasColumnName("level").HasMaxLength(32);
            e.Property(x => x.BranchId).HasColumnName("branch_id");
            e.Property(x => x.InstanceId).HasColumnName("instance_id");
            e.Property(x => x.TriggerSource).HasColumnName("trigger_source").HasMaxLength(32);
            e.Property(x => x.TriggerConfigJson).HasColumnName("trigger_config_json");
            e.Property(x => x.ComparerSource).HasColumnName("comparer_source").HasMaxLength(32);
            e.Property(x => x.ComparisonOptionsJson).HasColumnName("comparison_options_json");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.Property(x => x.Version).HasColumnName("version");
            // Exactly one global row; at most one row per branch; at most one row per instance.
            e.HasIndex(x => x.Level).IsUnique().HasFilter("[level] = 'Global'");
            e.HasIndex(x => x.BranchId).IsUnique().HasFilter("[branch_id] IS NOT NULL");
            e.HasIndex(x => x.InstanceId).IsUnique().HasFilter("[instance_id] IS NOT NULL");
        });

        // OpenIddict's applications/authorizations/scopes/tokens tables share this context, so the
        // existing migration runner creates and versions them (replaces the old AuthDbContext + EnsureCreated).
        b.UseOpenIddict();
    }
}
