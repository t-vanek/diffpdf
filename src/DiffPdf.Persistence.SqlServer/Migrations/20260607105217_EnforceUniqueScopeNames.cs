using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DiffPdf.Persistence.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class EnforceUniqueScopeNames : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // De-duplicate existing names before adding the unique indexes: in each group keep the earliest row
            // and suffix the rest with their (unique) key, e.g. "Branch" -> "Branch (B53…)". Grouping is
            // case-insensitive (LOWER); the index below is case-insensitive via SQL Server's default collation.
            migrationBuilder.Sql(@"
WITH d AS (SELECT id, [key], name, ROW_NUMBER() OVER (PARTITION BY LOWER(name) ORDER BY created_at, id) AS rn FROM branches)
UPDATE b SET name = b.name + ' (' + b.[key] + ')' FROM branches b INNER JOIN d ON b.id = d.id WHERE d.rn > 1;");

            migrationBuilder.Sql(@"
WITH d AS (SELECT id, [key], name, ROW_NUMBER() OVER (PARTITION BY branch_id, LOWER(name) ORDER BY created_at, id) AS rn FROM instances)
UPDATE i SET name = i.name + ' (' + i.[key] + ')' FROM instances i INNER JOIN d ON i.id = d.id WHERE d.rn > 1;");

            // nvarchar(max) cannot be an index key column — bound the name so it can be indexed. 400 keeps the
            // composite (branch_id + name) key well under the limit and is far above any realistic name length.
            migrationBuilder.Sql(@"ALTER TABLE branches ALTER COLUMN name nvarchar(400) NOT NULL;");
            migrationBuilder.Sql(@"ALTER TABLE instances ALTER COLUMN name nvarchar(400) NOT NULL;");

            // Unique names: globally for branches, per-branch for instances. Case sensitivity follows the
            // database's default collation (case-insensitive on a standard install), matching the app-layer check.
            migrationBuilder.Sql(@"CREATE UNIQUE INDEX ix_branches_name_ci ON branches(name);");
            migrationBuilder.Sql(@"CREATE UNIQUE INDEX ix_instances_branch_id_name_ci ON instances(branch_id, name);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Drops the constraints; the one-time de-duplication renames cannot be reversed.
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS ix_branches_name_ci ON branches;");
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS ix_instances_branch_id_name_ci ON instances;");
            migrationBuilder.Sql(@"ALTER TABLE branches ALTER COLUMN name nvarchar(max) NOT NULL;");
            migrationBuilder.Sql(@"ALTER TABLE instances ALTER COLUMN name nvarchar(max) NOT NULL;");
        }
    }
}
