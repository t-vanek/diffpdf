using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DiffPdf.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class FixBranchScopeConfigUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_scope_configurations_branch_id",
                table: "scope_configurations");

            migrationBuilder.CreateIndex(
                name: "IX_scope_configurations_branch_id",
                table: "scope_configurations",
                column: "branch_id",
                unique: true,
                filter: "level = 'Branch'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_scope_configurations_branch_id",
                table: "scope_configurations");

            migrationBuilder.CreateIndex(
                name: "IX_scope_configurations_branch_id",
                table: "scope_configurations",
                column: "branch_id",
                unique: true,
                filter: "branch_id IS NOT NULL");
        }
    }
}
