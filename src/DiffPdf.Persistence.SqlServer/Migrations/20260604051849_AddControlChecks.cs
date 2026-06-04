using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DiffPdf.Persistence.SqlServer.Migrations
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
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    check_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    outcome = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    detail = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_control_check_runs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "control_checks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    key = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    name = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    type = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    scope_kind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    branch_key = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    instance_key = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    cron = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    interval_seconds = table.Column<int>(type: "int", nullable: true),
                    parameters_json = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    events_json = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    enabled = table.Column<bool>(type: "bit", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    last_run_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    last_outcome = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
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
