using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DiffPdf.Persistence.SqlServer.Migrations
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
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ErrorCount",
                table: "jobs",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "GatePassed",
                table: "jobs",
                type: "bit",
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
