using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DiffPdf.Persistence.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class SystemEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "system_events",
                columns: table => new
                {
                    seq = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    type = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    severity = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    branch_key = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    instance_key = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    job_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    automation_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    message = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    detail = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_system_events", x => x.seq);
                });

            migrationBuilder.CreateIndex(
                name: "IX_system_events_job_id",
                table: "system_events",
                column: "job_id");

            migrationBuilder.CreateIndex(
                name: "IX_system_events_occurred_at",
                table: "system_events",
                column: "occurred_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "system_events");
        }
    }
}
