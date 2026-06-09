using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DiffPdf.Persistence.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddFilePairTaskPagingIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "relative_path",
                table: "file_pair_tasks",
                type: "nvarchar(400)",
                maxLength: 400,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)");

            migrationBuilder.AlterColumn<string>(
                name: "ResultStatus",
                table: "file_pair_tasks",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_file_pair_tasks_job_id_relative_path",
                table: "file_pair_tasks",
                columns: new[] { "job_id", "relative_path" });

            migrationBuilder.CreateIndex(
                name: "IX_file_pair_tasks_job_id_ResultStatus",
                table: "file_pair_tasks",
                columns: new[] { "job_id", "ResultStatus" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_file_pair_tasks_job_id_relative_path",
                table: "file_pair_tasks");

            migrationBuilder.DropIndex(
                name: "IX_file_pair_tasks_job_id_ResultStatus",
                table: "file_pair_tasks");

            migrationBuilder.AlterColumn<string>(
                name: "relative_path",
                table: "file_pair_tasks",
                type: "nvarchar(max)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(400)",
                oldMaxLength: 400);

            migrationBuilder.AlterColumn<string>(
                name: "ResultStatus",
                table: "file_pair_tasks",
                type: "nvarchar(max)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(32)",
                oldMaxLength: 32,
                oldNullable: true);
        }
    }
}
