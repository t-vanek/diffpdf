using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DiffPdf.Persistence.SqlServer.Migrations
{
    /// <summary>
    /// Reworks the control plane of checks into the automation engine. <c>control_checks</c> becomes
    /// <c>automations</c> (a trigger + step-pipeline definition): the single <c>type</c>/<c>parameters_json</c>
    /// pair is wrapped into a one-step <c>steps_json</c> pipeline, and the new trigger
    /// (<c>event_triggers_json</c>, <c>event_debounce_seconds</c>), execution-policy (<c>timeout_seconds</c>,
    /// <c>max_attempts</c>, <c>retry_delay_seconds</c>, <c>failure_threshold</c>) and persisted engine-state
    /// columns (<c>next_run_at</c>, <c>running_since</c>, <c>consecutive_failures</c>,
    /// <c>last_event_triggered_at</c>) are added with their defaults. <c>control_check_runs</c> becomes
    /// <c>automation_runs</c> with trigger/attempt/step-result columns. The retired
    /// <c>CheckRecovered</c> notification event is renamed to <c>AutomationRecovered</c> in both the
    /// automations' and the notification subscriptions' event JSON. Hand-authored (not the generated
    /// drop/create) so existing rows keep their data; <c>next_run_at</c> stays NULL and is seeded by the
    /// engine's first leading tick without firing.
    /// </summary>
    public partial class ReworkAutomations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ---- 1. Renames (data + indexes preserved; PK/index NAMES renamed explicitly so the
            //         database matches the new model snapshot). ----
            migrationBuilder.RenameTable(name: "control_checks", newName: "automations");
            migrationBuilder.RenameTable(name: "control_check_runs", newName: "automation_runs");
            migrationBuilder.Sql("EXEC sp_rename 'PK_control_checks', 'PK_automations', 'OBJECT';");
            migrationBuilder.Sql("EXEC sp_rename 'PK_control_check_runs', 'PK_automation_runs', 'OBJECT';");
            migrationBuilder.RenameIndex(name: "IX_control_checks_key", table: "automations", newName: "IX_automations_key");
            migrationBuilder.RenameIndex(name: "IX_control_checks_enabled", table: "automations", newName: "IX_automations_enabled");
            migrationBuilder.RenameColumn(name: "check_id", table: "automation_runs", newName: "automation_id");
            migrationBuilder.RenameIndex(
                name: "IX_control_check_runs_check_id_started_at", table: "automation_runs",
                newName: "IX_automation_runs_automation_id_started_at");

            // ---- 2. New columns (named default constraints let the NOT NULL adds succeed on existing rows). ----
            migrationBuilder.Sql("alter table automations add steps_json nvarchar(max) not null constraint df_auto_steps default '[]';");
            migrationBuilder.Sql("alter table automations add event_triggers_json nvarchar(max) not null constraint df_auto_event_triggers default '[]';");
            migrationBuilder.Sql("alter table automations add event_debounce_seconds int not null constraint df_auto_event_debounce default 60;");
            migrationBuilder.Sql("alter table automations add timeout_seconds int not null constraint df_auto_timeout default 600;");
            migrationBuilder.Sql("alter table automations add max_attempts int not null constraint df_auto_max_attempts default 1;");
            migrationBuilder.Sql("alter table automations add retry_delay_seconds int not null constraint df_auto_retry_delay default 30;");
            migrationBuilder.Sql("alter table automations add failure_threshold int not null constraint df_auto_failure_threshold default 3;");
            migrationBuilder.Sql("alter table automations add consecutive_failures int not null constraint df_auto_consecutive_failures default 0;");
            migrationBuilder.Sql("alter table automations add next_run_at datetimeoffset null;");
            migrationBuilder.Sql("alter table automations add running_since datetimeoffset null;");
            migrationBuilder.Sql("alter table automations add last_event_triggered_at datetimeoffset null;");

            migrationBuilder.Sql("alter table automation_runs add trigger_kind nvarchar(32) not null constraint df_arun_trigger_kind default 'Schedule';");
            migrationBuilder.Sql("alter table automation_runs add trigger_detail nvarchar(max) null;");
            migrationBuilder.Sql("alter table automation_runs add attempt int not null constraint df_arun_attempt default 1;");
            migrationBuilder.Sql("alter table automation_runs add step_results_json nvarchar(max) not null constraint df_arun_step_results default '[]';");

            // ---- 3. Backfill: wrap the legacy single executor into a one-step pipeline. `type` holds a
            //         strict enum name and `parameters_json` a serializer-produced JSON object, so plain
            //         concatenation builds valid camelCase JSON (matching DiffPdfJson's output). ----
            migrationBuilder.Sql("""
                update automations
                set steps_json = '[{"type":"' + type + '","parameters":'
                                 + coalesce(nullif(ltrim(rtrim(parameters_json)), ''), '{}') + '}]';
                """);

            // The recovered-event rename: the value is a quoted enum token in JSON arrays, unique across
            // the enum set, so a string REPLACE is exact.
            migrationBuilder.Sql("update automations set events_json = replace(events_json, '\"CheckRecovered\"', '\"AutomationRecovered\"');");
            migrationBuilder.Sql("update notification_subscriptions set events_json = replace(events_json, '\"CheckRecovered\"', '\"AutomationRecovered\"');");

            // ---- 4. Drop the superseded columns. ----
            migrationBuilder.Sql("alter table automations drop column type;");
            migrationBuilder.Sql("alter table automations drop column parameters_json;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Restore the single-executor columns from the first pipeline step.
            migrationBuilder.Sql("alter table automations add type nvarchar(32) not null constraint df_auto_type default '';");
            migrationBuilder.Sql("alter table automations add parameters_json nvarchar(max) not null constraint df_auto_parameters default '{}';");
            migrationBuilder.Sql("""
                update automations
                set type = coalesce(json_value(steps_json, '$[0].type'), ''),
                    parameters_json = coalesce(json_query(steps_json, '$[0].parameters'), '{}');
                """);
            migrationBuilder.Sql("alter table automations drop constraint df_auto_type;");
            migrationBuilder.Sql("alter table automations drop constraint df_auto_parameters;");

            migrationBuilder.Sql("update automations set events_json = replace(events_json, '\"AutomationRecovered\"', '\"CheckRecovered\"');");
            migrationBuilder.Sql("update notification_subscriptions set events_json = replace(events_json, '\"AutomationRecovered\"', '\"CheckRecovered\"');");

            migrationBuilder.Sql("alter table automation_runs drop constraint df_arun_trigger_kind;");
            migrationBuilder.Sql("alter table automation_runs drop constraint df_arun_attempt;");
            migrationBuilder.Sql("alter table automation_runs drop constraint df_arun_step_results;");
            migrationBuilder.DropColumn(name: "trigger_kind", table: "automation_runs");
            migrationBuilder.DropColumn(name: "trigger_detail", table: "automation_runs");
            migrationBuilder.DropColumn(name: "attempt", table: "automation_runs");
            migrationBuilder.DropColumn(name: "step_results_json", table: "automation_runs");

            migrationBuilder.Sql("alter table automations drop constraint df_auto_steps;");
            migrationBuilder.Sql("alter table automations drop constraint df_auto_event_triggers;");
            migrationBuilder.Sql("alter table automations drop constraint df_auto_event_debounce;");
            migrationBuilder.Sql("alter table automations drop constraint df_auto_timeout;");
            migrationBuilder.Sql("alter table automations drop constraint df_auto_max_attempts;");
            migrationBuilder.Sql("alter table automations drop constraint df_auto_retry_delay;");
            migrationBuilder.Sql("alter table automations drop constraint df_auto_failure_threshold;");
            migrationBuilder.Sql("alter table automations drop constraint df_auto_consecutive_failures;");
            migrationBuilder.DropColumn(name: "steps_json", table: "automations");
            migrationBuilder.DropColumn(name: "event_triggers_json", table: "automations");
            migrationBuilder.DropColumn(name: "event_debounce_seconds", table: "automations");
            migrationBuilder.DropColumn(name: "timeout_seconds", table: "automations");
            migrationBuilder.DropColumn(name: "max_attempts", table: "automations");
            migrationBuilder.DropColumn(name: "retry_delay_seconds", table: "automations");
            migrationBuilder.DropColumn(name: "failure_threshold", table: "automations");
            migrationBuilder.DropColumn(name: "consecutive_failures", table: "automations");
            migrationBuilder.DropColumn(name: "next_run_at", table: "automations");
            migrationBuilder.DropColumn(name: "running_since", table: "automations");
            migrationBuilder.DropColumn(name: "last_event_triggered_at", table: "automations");

            migrationBuilder.RenameIndex(
                name: "IX_automation_runs_automation_id_started_at", table: "automation_runs",
                newName: "IX_control_check_runs_check_id_started_at");
            migrationBuilder.RenameColumn(name: "automation_id", table: "automation_runs", newName: "check_id");
            migrationBuilder.RenameIndex(name: "IX_automations_enabled", table: "automations", newName: "IX_control_checks_enabled");
            migrationBuilder.RenameIndex(name: "IX_automations_key", table: "automations", newName: "IX_control_checks_key");
            migrationBuilder.Sql("EXEC sp_rename 'PK_automation_runs', 'PK_control_check_runs', 'OBJECT';");
            migrationBuilder.Sql("EXEC sp_rename 'PK_automations', 'PK_control_checks', 'OBJECT';");
            migrationBuilder.RenameTable(name: "automation_runs", newName: "control_check_runs");
            migrationBuilder.RenameTable(name: "automations", newName: "control_checks");
        }
    }
}
