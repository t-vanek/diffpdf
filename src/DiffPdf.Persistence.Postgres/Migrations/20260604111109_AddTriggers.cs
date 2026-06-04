using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DiffPdf.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddTriggers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "source",
                table: "jobs",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "trigger_id",
                table: "jobs",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "audit_log",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    actor = table.Column<string>(type: "text", nullable: true),
                    source = table.Column<string>(type: "text", nullable: false),
                    action = table.Column<string>(type: "text", nullable: false),
                    entity_type = table.Column<string>(type: "text", nullable: false),
                    entity_id = table.Column<string>(type: "text", nullable: true),
                    detail = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_log", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "trigger_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    trigger_id = table.Column<Guid>(type: "uuid", nullable: false),
                    batch_job_id = table.Column<Guid>(type: "uuid", nullable: true),
                    source = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    result = table.Column<string>(type: "text", nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    duration_ms = table.Column<long>(type: "bigint", nullable: true),
                    error = table.Column<string>(type: "text", nullable: true),
                    requested_by = table.Column<string>(type: "text", nullable: true),
                    idempotency_key = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_trigger_runs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "triggers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    instance_id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch_key = table.Column<string>(type: "text", nullable: false),
                    instance_key = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    action_type = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    is_default = table.Column<bool>(type: "boolean", nullable: false),
                    spec_json = table.Column<string>(type: "jsonb", nullable: false),
                    run_count = table.Column<int>(type: "integer", nullable: false),
                    last_run_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_outcome = table.Column<string>(type: "text", nullable: true),
                    created_by = table.Column<string>(type: "text", nullable: true),
                    updated_by = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_triggers", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_jobs_trigger_id",
                table: "jobs",
                column: "trigger_id");

            migrationBuilder.CreateIndex(
                name: "IX_audit_log_at",
                table: "audit_log",
                column: "at");

            migrationBuilder.CreateIndex(
                name: "IX_audit_log_entity_type_entity_id_at",
                table: "audit_log",
                columns: new[] { "entity_type", "entity_id", "at" });

            migrationBuilder.CreateIndex(
                name: "IX_trigger_runs_trigger_id_idempotency_key",
                table: "trigger_runs",
                columns: new[] { "trigger_id", "idempotency_key" },
                unique: true,
                filter: "idempotency_key IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_trigger_runs_trigger_id_started_at",
                table: "trigger_runs",
                columns: new[] { "trigger_id", "started_at" });

            migrationBuilder.CreateIndex(
                name: "IX_triggers_branch_id",
                table: "triggers",
                column: "branch_id");

            migrationBuilder.CreateIndex(
                name: "IX_triggers_instance_id",
                table: "triggers",
                column: "instance_id",
                unique: true,
                filter: "is_default AND NOT is_deleted");

            migrationBuilder.CreateIndex(
                name: "IX_triggers_instance_id_status",
                table: "triggers",
                columns: new[] { "instance_id", "status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_log");

            migrationBuilder.DropTable(
                name: "trigger_runs");

            migrationBuilder.DropTable(
                name: "triggers");

            migrationBuilder.DropIndex(
                name: "IX_jobs_trigger_id",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "source",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "trigger_id",
                table: "jobs");
        }
    }
}
