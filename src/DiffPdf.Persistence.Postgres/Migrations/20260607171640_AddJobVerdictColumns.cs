using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DiffPdf.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddJobVerdictColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DifferingCount",
                table: "jobs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ErrorCount",
                table: "jobs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "GatePassed",
                table: "jobs",
                type: "boolean",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DifferingCount",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "ErrorCount",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "GatePassed",
                table: "jobs");
        }
    }
}
