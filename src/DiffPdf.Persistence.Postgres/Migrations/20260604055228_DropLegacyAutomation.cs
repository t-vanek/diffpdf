using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DiffPdf.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class DropLegacyAutomation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Drop child tables before parents. schedule_runs has a FK to comparison_schedules
            // (schedule_id ... references comparison_schedules(id) on delete cascade), defined in raw
            // SQL in InitialCreate and never tracked by the EF model snapshot — so DropTable cannot
            // auto-order it. Dropping the parent first fails because the table is still referenced by
            // a foreign key constraint. folder_watches only references branches/instances (not dropped
            // here), so it is independent.
            migrationBuilder.DropTable(
                name: "folder_watches");

            migrationBuilder.DropTable(
                name: "schedule_runs");

            migrationBuilder.DropTable(
                name: "comparison_schedules");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "comparison_schedules",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch_key = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    cron = table.Column<string>(type: "text", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    gate_json = table.Column<string>(type: "jsonb", nullable: true),
                    instance_id = table.Column<Guid>(type: "uuid", nullable: false),
                    instance_key = table.Column<string>(type: "text", nullable: false),
                    key = table.Column<string>(type: "text", nullable: false),
                    last_run_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    max_degree_of_parallelism = table.Column<int>(type: "integer", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    options_json = table.Column<string>(type: "jsonb", nullable: false),
                    recursive = table.Column<bool>(type: "boolean", nullable: false),
                    search_pattern = table.Column<string>(type: "text", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_comparison_schedules", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "folder_watches",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch_key = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    instance_id = table.Column<Guid>(type: "uuid", nullable: false),
                    instance_key = table.Column<string>(type: "text", nullable: false),
                    last_triggered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    stability_seconds = table.Column<int>(type: "integer", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_folder_watches", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "schedule_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    differing = table.Column<int>(type: "integer", nullable: false),
                    error = table.Column<string>(type: "text", nullable: true),
                    errors = table.Column<int>(type: "integer", nullable: false),
                    files_with_content_errors = table.Column<int>(type: "integer", nullable: false),
                    gate_violations_json = table.Column<string>(type: "jsonb", nullable: true),
                    job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    outcome = table.Column<string>(type: "text", nullable: false),
                    passed = table.Column<bool>(type: "boolean", nullable: false),
                    schedule_id = table.Column<Guid>(type: "uuid", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_schedule_runs", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_comparison_schedules_enabled",
                table: "comparison_schedules",
                column: "enabled");

            migrationBuilder.CreateIndex(
                name: "IX_comparison_schedules_instance_id_key",
                table: "comparison_schedules",
                columns: new[] { "instance_id", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_folder_watches_enabled",
                table: "folder_watches",
                column: "enabled");

            migrationBuilder.CreateIndex(
                name: "IX_folder_watches_instance_id",
                table: "folder_watches",
                column: "instance_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_schedule_runs_job_id",
                table: "schedule_runs",
                column: "job_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_schedule_runs_schedule_id_started_at",
                table: "schedule_runs",
                columns: new[] { "schedule_id", "started_at" });
        }
    }
}
