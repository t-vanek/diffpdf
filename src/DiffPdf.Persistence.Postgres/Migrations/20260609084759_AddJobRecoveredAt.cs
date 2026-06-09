using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DiffPdf.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddJobRecoveredAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RecoveredAt",
                table: "jobs",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RecoveredAt",
                table: "jobs");
        }
    }
}
