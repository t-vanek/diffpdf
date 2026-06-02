using DiffPdf.Persistence.Postgres.Entities;
using Microsoft.EntityFrameworkCore;

namespace DiffPdf.Persistence.Postgres;

public sealed class DiffPdfDbContext(DbContextOptions<DiffPdfDbContext> options) : DbContext(options)
{
    public DbSet<BusinessInstanceEntity> BusinessInstances => Set<BusinessInstanceEntity>();
    public DbSet<ProjectEntity> Projects => Set<ProjectEntity>();
    public DbSet<JobEntity> Jobs => Set<JobEntity>();
    public DbSet<FilePairTaskEntity> FilePairTasks => Set<FilePairTaskEntity>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<BusinessInstanceEntity>(e =>
        {
            e.ToTable("business_instances");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Key).HasColumnName("key");
            e.Property(x => x.Name).HasColumnName("name");
            e.Property(x => x.Enabled).HasColumnName("enabled");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.Property(x => x.Version).HasColumnName("version");
            e.HasIndex(x => x.Key).IsUnique();
        });

        b.Entity<ProjectEntity>(e =>
        {
            e.ToTable("projects");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.BusinessInstanceId).HasColumnName("business_instance_id");
            e.Property(x => x.Key).HasColumnName("key");
            e.Property(x => x.Name).HasColumnName("name");
            e.Property(x => x.Enabled).HasColumnName("enabled");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.Property(x => x.Version).HasColumnName("version");
            e.HasIndex(x => new { x.BusinessInstanceId, x.Key }).IsUnique();
        });

        b.Entity<JobEntity>(e =>
        {
            e.ToTable("jobs");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.BusinessInstanceId).HasColumnName("business_instance_id");
            e.Property(x => x.ProjectId).HasColumnName("project_id");
            e.Property(x => x.Status).HasColumnName("status");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.Property(x => x.StartedAt).HasColumnName("started_at");
            e.Property(x => x.CompletedAt).HasColumnName("completed_at");
            e.Property(x => x.ProcessedCount).HasColumnName("processed_count");
            e.Property(x => x.TotalCount).HasColumnName("total_count");
            e.Property(x => x.RequestJson).HasColumnName("request_json").HasColumnType("jsonb");
            e.Property(x => x.ReportJson).HasColumnName("report_json").HasColumnType("jsonb");
            e.Property(x => x.Error).HasColumnName("error");
            e.Property(x => x.Version).HasColumnName("version");
            e.Property(x => x.LockedBy).HasColumnName("locked_by");
            e.Property(x => x.LockedUntil).HasColumnName("locked_until");
            e.HasIndex(x => new { x.BusinessInstanceId, x.ProjectId, x.CreatedAt });
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
            e.Property(x => x.Status).HasColumnName("status");
            e.Property(x => x.AttemptCount).HasColumnName("attempt_count");
            e.Property(x => x.Error).HasColumnName("error");
            e.Property(x => x.ResultJson).HasColumnName("result_json").HasColumnType("jsonb");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.StartedAt).HasColumnName("started_at");
            e.Property(x => x.CompletedAt).HasColumnName("completed_at");
            e.Property(x => x.Version).HasColumnName("version");
            e.Property(x => x.LockedBy).HasColumnName("locked_by");
            e.Property(x => x.LockedUntil).HasColumnName("locked_until");
            e.HasIndex(x => new { x.JobId, x.Status });
        });
    }
}
