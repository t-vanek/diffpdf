using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DiffPdf.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class EnforceUniqueScopeNames : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // De-duplicate existing names (case-insensitive) before adding the unique indexes: in each group
            // keep the earliest row and suffix the rest with their (unique) key, e.g. "Branch" -> "Branch (B53…)".
            migrationBuilder.Sql(@"
UPDATE branches b
SET name = b.name || ' (' || b.key || ')'
FROM (SELECT id, row_number() OVER (PARTITION BY lower(name) ORDER BY created_at, id) AS rn FROM branches) d
WHERE b.id = d.id AND d.rn > 1;");

            migrationBuilder.Sql(@"
UPDATE instances i
SET name = i.name || ' (' || i.key || ')'
FROM (SELECT id, row_number() OVER (PARTITION BY branch_id, lower(name) ORDER BY created_at, id) AS rn FROM instances) d
WHERE i.id = d.id AND d.rn > 1;");

            // Case-insensitive unique names: globally for branches, per-branch for instances.
            migrationBuilder.Sql(@"CREATE UNIQUE INDEX ix_branches_name_ci ON branches (lower(name));");
            migrationBuilder.Sql(@"CREATE UNIQUE INDEX ix_instances_branch_id_name_ci ON instances (branch_id, lower(name));");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Drops the constraints; the one-time de-duplication renames cannot be reversed.
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS ix_branches_name_ci;");
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS ix_instances_branch_id_name_ci;");
        }
    }
}
