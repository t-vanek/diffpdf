using DiffPdf.Persistence.SqlServer.Entities;
using Microsoft.EntityFrameworkCore;

namespace DiffPdf.Persistence.SqlServer;

public sealed class DiffPdfDbContext(DbContextOptions<DiffPdfDbContext> options) : DbContext(options)
{
    public DbSet<BranchEntity> Branches => Set<BranchEntity>();
    public DbSet<InstanceEntity> Instances => Set<InstanceEntity>();
    public DbSet<JobEntity> Jobs => Set<JobEntity>();
    public DbSet<FilePairTaskEntity> FilePairTasks => Set<FilePairTaskEntity>();
    public DbSet<ScheduleEntity> Schedules => Set<ScheduleEntity>();
    public DbSet<SubscriptionEntity> Subscriptions => Set<SubscriptionEntity>();
    public DbSet<ScheduleRunEntity> ScheduleRuns => Set<ScheduleRunEntity>();

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
            e.HasIndex(x => new { x.BranchId, x.InstanceId, x.CreatedAt });
            e.HasIndex(x => new { x.Status, x.CreatedAt });
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

        b.Entity<ScheduleEntity>(e =>
        {
            e.ToTable("comparison_schedules");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.BranchId).HasColumnName("branch_id");
            e.Property(x => x.InstanceId).HasColumnName("instance_id");
            e.Property(x => x.BranchKey).HasColumnName("branch_key").HasMaxLength(256);
            e.Property(x => x.InstanceKey).HasColumnName("instance_key").HasMaxLength(256);
            e.Property(x => x.Key).HasColumnName("key").HasMaxLength(256);
            e.Property(x => x.Name).HasColumnName("name");
            e.Property(x => x.Cron).HasColumnName("cron").HasMaxLength(256);
            e.Property(x => x.OptionsJson).HasColumnName("options_json");
            e.Property(x => x.GateJson).HasColumnName("gate_json");
            e.Property(x => x.SearchPattern).HasColumnName("search_pattern").HasMaxLength(256);
            e.Property(x => x.Recursive).HasColumnName("recursive");
            e.Property(x => x.MaxDegreeOfParallelism).HasColumnName("max_degree_of_parallelism");
            e.Property(x => x.Enabled).HasColumnName("enabled");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.Property(x => x.LastRunAt).HasColumnName("last_run_at");
            e.Property(x => x.Version).HasColumnName("version");
            e.HasIndex(x => new { x.InstanceId, x.Key }).IsUnique();
            e.HasIndex(x => x.Enabled);
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

        b.Entity<ScheduleRunEntity>(e =>
        {
            e.ToTable("schedule_runs");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.ScheduleId).HasColumnName("schedule_id");
            e.Property(x => x.JobId).HasColumnName("job_id");
            e.Property(x => x.StartedAt).HasColumnName("started_at");
            e.Property(x => x.CompletedAt).HasColumnName("completed_at");
            e.Property(x => x.Outcome).HasColumnName("outcome").HasMaxLength(32);
            e.Property(x => x.Differing).HasColumnName("differing");
            e.Property(x => x.Errors).HasColumnName("errors");
            e.Property(x => x.FilesWithContentErrors).HasColumnName("files_with_content_errors");
            e.Property(x => x.Passed).HasColumnName("passed");
            e.Property(x => x.GateViolationsJson).HasColumnName("gate_violations_json");
            e.Property(x => x.Error).HasColumnName("error");
            e.HasIndex(x => new { x.ScheduleId, x.StartedAt });
            e.HasIndex(x => x.JobId).IsUnique();
        });
    }
}
