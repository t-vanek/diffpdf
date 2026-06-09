using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DiffPdf.Persistence.SqlServer.Migrations
{
    /// <summary>
    /// Reworks notifications into an e-mail-only, multi-recipient rule model and adds the runtime SMTP settings
    /// table. <c>notification_subscriptions</c> loses <c>channel</c> / <c>target</c> (single) / <c>branch_key</c> /
    /// <c>instance_key</c> and gains <c>name</c> + JSON-array columns <c>recipients_json</c> / <c>branch_keys_json</c>
    /// / <c>instance_keys_json</c>. Existing <c>smtp</c> rows are backfilled into the arrays; <c>webhook</c> rows are
    /// dropped (the webhook channel is removed). Hand-authored (not the generated drop/rename) so existing rows keep
    /// their data.
    /// </summary>
    public partial class ReworkNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Webhook rows can't become e-mail rules (their target is a URL, not an address).
            migrationBuilder.Sql("delete from notification_subscriptions where channel = 'webhook';");

            // New columns (defaults let the NOT NULL add succeed on existing rows; rows are then backfilled).
            migrationBuilder.Sql("alter table notification_subscriptions add name nvarchar(max) not null constraint df_ns_name default '';");
            migrationBuilder.Sql("alter table notification_subscriptions add recipients_json nvarchar(max) not null constraint df_ns_recipients default '[]';");
            migrationBuilder.Sql("alter table notification_subscriptions add branch_keys_json nvarchar(max) not null constraint df_ns_branch_keys default '[]';");
            migrationBuilder.Sql("alter table notification_subscriptions add instance_keys_json nvarchar(max) not null constraint df_ns_instance_keys default '[]';");

            // Backfill the JSON arrays from the old single-value columns (escaping any embedded double quotes).
            migrationBuilder.Sql("""
                update notification_subscriptions
                set recipients_json = '["' + replace(target, '"', '\"') + '"]'
                where target is not null and target <> '';
                """);
            migrationBuilder.Sql("""
                update notification_subscriptions
                set branch_keys_json = '["' + replace(branch_key, '"', '\"') + '"]'
                where branch_key is not null and branch_key <> '';
                """);
            migrationBuilder.Sql("""
                update notification_subscriptions
                set instance_keys_json = '["' + replace(instance_key, '"', '\"') + '"]'
                where instance_key is not null and instance_key <> '';
                """);

            // Drop the superseded columns (none carry a default constraint or index — see InitialCreate).
            migrationBuilder.Sql("alter table notification_subscriptions drop column channel;");
            migrationBuilder.Sql("alter table notification_subscriptions drop column target;");
            migrationBuilder.Sql("alter table notification_subscriptions drop column branch_key;");
            migrationBuilder.Sql("alter table notification_subscriptions drop column instance_key;");

            // Single-row SMTP transport + sender settings (runtime-managed e-mail transport).
            migrationBuilder.CreateTable(
                name: "email_settings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    host = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    port = table.Column<int>(type: "int", nullable: false),
                    use_ssl = table.Column<bool>(type: "bit", nullable: false),
                    username = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    password = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    from_address = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    from_name = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_email_settings", x => x.id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "email_settings");

            // Re-add the old single-value columns (data is not restored).
            migrationBuilder.Sql("alter table notification_subscriptions add channel nvarchar(256) not null constraint df_ns_channel_down default 'smtp';");
            migrationBuilder.Sql("alter table notification_subscriptions add target nvarchar(max) not null constraint df_ns_target_down default '';");
            migrationBuilder.Sql("alter table notification_subscriptions add branch_key nvarchar(256) null;");
            migrationBuilder.Sql("alter table notification_subscriptions add instance_key nvarchar(256) null;");

            // Drop the rule-model columns (and their named default constraints first).
            migrationBuilder.Sql("alter table notification_subscriptions drop constraint df_ns_name;");
            migrationBuilder.Sql("alter table notification_subscriptions drop constraint df_ns_recipients;");
            migrationBuilder.Sql("alter table notification_subscriptions drop constraint df_ns_branch_keys;");
            migrationBuilder.Sql("alter table notification_subscriptions drop constraint df_ns_instance_keys;");
            migrationBuilder.Sql("alter table notification_subscriptions drop column name;");
            migrationBuilder.Sql("alter table notification_subscriptions drop column recipients_json;");
            migrationBuilder.Sql("alter table notification_subscriptions drop column branch_keys_json;");
            migrationBuilder.Sql("alter table notification_subscriptions drop column instance_keys_json;");
        }
    }
}
