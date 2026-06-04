using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DiffPdf.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddControlChecks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "control_check_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    check_id = table.Column<Guid>(type: "uuid", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    outcome = table.Column<string>(type: "text", nullable: false),
                    detail = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_control_check_runs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "control_checks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    type = table.Column<string>(type: "text", nullable: false),
                    scope_kind = table.Column<string>(type: "text", nullable: false),
                    branch_key = table.Column<string>(type: "text", nullable: true),
                    instance_key = table.Column<string>(type: "text", nullable: true),
                    cron = table.Column<string>(type: "text", nullable: true),
                    interval_seconds = table.Column<int>(type: "integer", nullable: true),
                    parameters_json = table.Column<string>(type: "jsonb", nullable: false),
                    events_json = table.Column<string>(type: "jsonb", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_run_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_outcome = table.Column<string>(type: "text", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_control_checks", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_control_check_runs_check_id_started_at",
                table: "control_check_runs",
                columns: new[] { "check_id", "started_at" });

            migrationBuilder.CreateIndex(
                name: "IX_control_checks_enabled",
                table: "control_checks",
                column: "enabled");

            migrationBuilder.CreateIndex(
                name: "IX_control_checks_key",
                table: "control_checks",
                column: "key",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "control_check_runs");

            migrationBuilder.DropTable(
                name: "control_checks");
        }
    }
}
