using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DiffPdf.Persistence.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class BackfillJobSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The AddTriggers migration added jobs.source as NOT NULL DEFAULT '', so every job row that
            // pre-dated it was backfilled with an empty string. That is not a valid JobSource name and
            // crashed the read path's enum parse. Normalize those rows to System (the "unspecified" bucket).
            migrationBuilder.Sql("update jobs set source = 'System' where source = '' or source is null;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Irreversible data repair: the original empty-string values cannot be reconstructed.
        }
    }
}
