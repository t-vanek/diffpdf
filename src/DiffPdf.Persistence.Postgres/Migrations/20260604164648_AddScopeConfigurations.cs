using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DiffPdf.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddScopeConfigurations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "scope_configurations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    level = table.Column<string>(type: "text", nullable: false),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    instance_id = table.Column<Guid>(type: "uuid", nullable: true),
                    trigger_source = table.Column<string>(type: "text", nullable: false),
                    trigger_config_json = table.Column<string>(type: "jsonb", nullable: false),
                    comparer_source = table.Column<string>(type: "text", nullable: false),
                    comparison_options_json = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_scope_configurations", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_scope_configurations_branch_id",
                table: "scope_configurations",
                column: "branch_id",
                unique: true,
                filter: "branch_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_scope_configurations_instance_id",
                table: "scope_configurations",
                column: "instance_id",
                unique: true,
                filter: "instance_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_scope_configurations_level",
                table: "scope_configurations",
                column: "level",
                unique: true,
                filter: "level = 'Global'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "scope_configurations");
        }
    }
}
