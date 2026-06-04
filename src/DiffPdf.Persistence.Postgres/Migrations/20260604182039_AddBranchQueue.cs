using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DiffPdf.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddBranchQueue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "priority",
                table: "jobs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "queue_paused",
                table: "branches",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_jobs_branch_id_status",
                table: "jobs",
                columns: new[] { "branch_id", "status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_jobs_branch_id_status",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "priority",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "queue_paused",
                table: "branches");
        }
    }
}
