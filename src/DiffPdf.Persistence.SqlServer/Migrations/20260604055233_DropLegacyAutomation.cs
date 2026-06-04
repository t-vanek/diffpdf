using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DiffPdf.Persistence.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class DropLegacyAutomation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "comparison_schedules");

            migrationBuilder.DropTable(
                name: "folder_watches");

            migrationBuilder.DropTable(
                name: "schedule_runs");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "comparison_schedules",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    branch_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    branch_key = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    cron = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    enabled = table.Column<bool>(type: "bit", nullable: false),
                    gate_json = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    instance_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    instance_key = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    key = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    last_run_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    max_degree_of_parallelism = table.Column<int>(type: "int", nullable: false),
                    name = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    options_json = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    recursive = table.Column<bool>(type: "bit", nullable: false),
                    search_pattern = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
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
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    branch_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    branch_key = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    enabled = table.Column<bool>(type: "bit", nullable: false),
                    instance_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    instance_key = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    last_triggered_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    stability_seconds = table.Column<int>(type: "int", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
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
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    differing = table.Column<int>(type: "int", nullable: false),
                    error = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    errors = table.Column<int>(type: "int", nullable: false),
                    files_with_content_errors = table.Column<int>(type: "int", nullable: false),
                    gate_violations_json = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    job_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    outcome = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    passed = table.Column<bool>(type: "bit", nullable: false),
                    schedule_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
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
