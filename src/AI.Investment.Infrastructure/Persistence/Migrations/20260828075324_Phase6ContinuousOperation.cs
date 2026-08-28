using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AI.Investment.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase6ContinuousOperation : Migration
    {
        /// <inheritdoc />

    // CA1861: a constant array passed as an argument is allocated on every call, and the analyzer
    // is enforced repository-wide. The scaffolded file was corrected rather than the rule exempted -
    // the column lists below are exactly what EF generated, hoisted to fields and nothing else.
    private static readonly string[] AutonomyGrantLookup = ["capability", "environment", "expires_at_utc"];

    private static readonly string[] EscalationsOutstanding = ["resolved_at_utc", "expires_at_utc"];

    private static readonly string[] CyclesRunnable = ["status", "updated_at_utc"];

    private static readonly string[] CyclesByWatch = ["watch_id", "started_at_utc"];

    private static readonly string[] OutboxDispatch = ["status", "next_attempt_at_utc"];

    private static readonly string[] WatchesByTrigger = ["trigger_type", "enabled"];

        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "autonomy_grants",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    capability = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    action_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    environment = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    granted_mode = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    demoted_mode = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    max_risk_tier = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    max_exposure = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    max_exposure_currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    limit_set = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    granted_by = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    granted_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    expires_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    revoked_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    revocation_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    demoted_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    demotion_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    demotion_count = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_autonomy_grants", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "escalations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    cycle_id = table.Column<Guid>(type: "uuid", nullable: true),
                    proposal_id = table.Column<Guid>(type: "uuid", nullable: true),
                    capability = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    reason = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    explanation = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    raised_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    expires_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    acknowledged_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    acknowledged_by = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    resolved_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    resolution = table.Column<string>(type: "character varying(622)", maxLength: 622, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_escalations", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "operating_cycles",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    correlation_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    capability = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    template = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    trigger_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    watch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    stage = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    budget = table.Column<string>(type: "jsonb", nullable: false),
                    consumption = table.Column<string>(type: "jsonb", nullable: false),
                    started_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    stopped_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    stopped_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    lease_owner = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    lease_expires_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    escalation_count = table.Column<int>(type: "integer", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_operating_cycles", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "outbox_messages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    message_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    dedup_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    correlation_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    cycle_id = table.Column<Guid>(type: "uuid", nullable: true),
                    occurred_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    dispatched_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    lease_owner = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    lease_expires_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outbox_messages", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "shadow_decisions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    cycle_id = table.Column<Guid>(type: "uuid", nullable: true),
                    proposal_id = table.Column<Guid>(type: "uuid", nullable: false),
                    capability = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    action_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    risk_tier = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    exposure = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    exposure_currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    actual_mode = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    actual_outcome = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    shadow_mode = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    shadow_outcome = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    recorded_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_shadow_decisions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "watches",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    target_kind = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    target_identifier = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    trigger_type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    condition_comparison = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    condition_threshold = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    condition_interval = table.Column<TimeSpan>(type: "interval", nullable: true),
                    cooldown = table.Column<TimeSpan>(type: "interval", nullable: false),
                    max_signal_age = table.Column<TimeSpan>(type: "interval", nullable: false),
                    priority = table.Column<int>(type: "integer", nullable: false),
                    capability = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    cycle_template = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    last_fired_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    fire_count = table.Column<int>(type: "integer", nullable: false),
                    disabled_reason = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_watches", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_autonomy_grants_lookup",
                table: "autonomy_grants",
                columns: AutonomyGrantLookup);

            migrationBuilder.CreateIndex(
                name: "ix_escalations_outstanding",
                table: "escalations",
                columns: EscalationsOutstanding);

            migrationBuilder.CreateIndex(
                name: "ix_operating_cycles_runnable",
                table: "operating_cycles",
                columns: CyclesRunnable);

            migrationBuilder.CreateIndex(
                name: "ix_operating_cycles_watch",
                table: "operating_cycles",
                columns: CyclesByWatch);

            migrationBuilder.CreateIndex(
                name: "ux_operating_cycles_trigger_key",
                table: "operating_cycles",
                column: "trigger_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_dispatch",
                table: "outbox_messages",
                columns: OutboxDispatch);

            migrationBuilder.CreateIndex(
                name: "ux_outbox_messages_dedup_key",
                table: "outbox_messages",
                column: "dedup_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_shadow_decisions_recorded",
                table: "shadow_decisions",
                column: "recorded_at_utc");

            migrationBuilder.CreateIndex(
                name: "ix_watches_trigger",
                table: "watches",
                columns: WatchesByTrigger);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "autonomy_grants");

            migrationBuilder.DropTable(
                name: "escalations");

            migrationBuilder.DropTable(
                name: "operating_cycles");

            migrationBuilder.DropTable(
                name: "outbox_messages");

            migrationBuilder.DropTable(
                name: "shadow_decisions");

            migrationBuilder.DropTable(
                name: "watches");
        }
    }
}
