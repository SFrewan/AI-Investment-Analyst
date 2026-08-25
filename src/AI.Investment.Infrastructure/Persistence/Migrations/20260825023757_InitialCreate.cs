using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AI.Investment.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "action_executions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    proposal_id = table.Column<Guid>(type: "uuid", nullable: false),
                    decision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    correlation_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    action_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    capability = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    started_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    completed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    failure_reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_action_executions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "audit_records",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    correlation_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    occurred_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    event_type = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    actor = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    actor_kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    summary = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    proposal_id = table.Column<Guid>(type: "uuid", nullable: true),
                    decision_id = table.Column<Guid>(type: "uuid", nullable: true),
                    execution_id = table.Column<Guid>(type: "uuid", nullable: true),
                    capability = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    action_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    outcome = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    risk_tier = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    details = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_records", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "companies",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ticker = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    exchange = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: true),
                    sector = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    industry = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    country = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_companies", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "processed_actions",
                columns: table => new
                {
                    idempotency_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    proposal_id = table.Column<Guid>(type: "uuid", nullable: false),
                    claimed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_processed_actions", x => x.idempotency_key);
                });

            migrationBuilder.CreateIndex(
                name: "ix_action_executions_correlation_id",
                table: "action_executions",
                column: "correlation_id");

            migrationBuilder.CreateIndex(
                name: "ix_action_executions_proposal_id",
                table: "action_executions",
                column: "proposal_id");

            migrationBuilder.CreateIndex(
                name: "ix_action_executions_started_at_utc",
                table: "action_executions",
                column: "started_at_utc");

            migrationBuilder.CreateIndex(
                name: "ix_audit_records_correlation_id",
                table: "audit_records",
                column: "correlation_id");

            migrationBuilder.CreateIndex(
                name: "ix_audit_records_occurred_at_utc",
                table: "audit_records",
                column: "occurred_at_utc");

            migrationBuilder.CreateIndex(
                name: "ix_audit_records_proposal_id",
                table: "audit_records",
                column: "proposal_id");

            migrationBuilder.CreateIndex(
                name: "ix_companies_name",
                table: "companies",
                column: "name");

            migrationBuilder.CreateIndex(
                name: "ix_companies_ticker",
                table: "companies",
                column: "ticker",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_processed_actions_claimed_at_utc",
                table: "processed_actions",
                column: "claimed_at_utc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "action_executions");

            migrationBuilder.DropTable(
                name: "audit_records");

            migrationBuilder.DropTable(
                name: "companies");

            migrationBuilder.DropTable(
                name: "processed_actions");
        }
    }
}
