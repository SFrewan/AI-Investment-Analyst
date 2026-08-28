using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AI.Investment.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase5OpportunityApprovalCapital : Migration
    {
        // Hoisted out of the CreateIndex calls the scaffolder generated. CA1861 flags a constant
        // array passed to a method that is called repeatedly, and this repository builds with
        // warnings as errors - so a generated file is corrected rather than exempted. The column
        // lists themselves are unchanged.
        private static readonly string[] ApprovalTokensByOpportunity = ["opportunity_id", "consumed_at_utc"];
        private static readonly string[] OpportunitiesByStatus = ["status", "status_changed_at_utc"];
        private static readonly string[] OpportunitiesBySubject = ["subject_kind", "subject_identifier"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "approval_tokens",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    opportunity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    proposal_id = table.Column<Guid>(type: "uuid", nullable: false),
                    action_fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    max_amount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    max_amount_currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    approved_by = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    issued_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    expires_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    consumed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    revoked_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    revocation_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_approval_tokens", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "kill_switch",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    capability = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    engaged = table.Column<bool>(type: "boolean", nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_kill_switch", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "ledger_entries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    debit_account = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    debit_account_kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    credit_account = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    credit_account_kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    occurred_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    opportunity_id = table.Column<Guid>(type: "uuid", nullable: true),
                    execution_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ledger_entries", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "opportunities",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    subject_kind = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    subject_identifier = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    discoverer_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    discovered_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    detail_type = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    detail = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    economics = table.Column<string>(type: "jsonb", nullable: true),
                    risk = table.Column<string>(type: "jsonb", nullable: true),
                    confidence = table.Column<decimal>(type: "numeric(5,4)", precision: 5, scale: 4, nullable: true),
                    score_metric = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    score_value = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    score_version = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    score_as_of_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    approval_token_id = table.Column<Guid>(type: "uuid", nullable: true),
                    execution_id = table.Column<Guid>(type: "uuid", nullable: true),
                    resolution = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    status_changed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    evidence = table.Column<string>(type: "jsonb", nullable: false),
                    proposal_ids = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_opportunities", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_approval_tokens_opportunity",
                table: "approval_tokens",
                columns: ApprovalTokensByOpportunity);

            migrationBuilder.CreateIndex(
                name: "ux_kill_switch_capability",
                table: "kill_switch",
                column: "capability",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_ledger_entries_occurred_at_utc",
                table: "ledger_entries",
                column: "occurred_at_utc");

            migrationBuilder.CreateIndex(
                name: "ix_ledger_entries_opportunity",
                table: "ledger_entries",
                column: "opportunity_id");

            migrationBuilder.CreateIndex(
                name: "ix_opportunities_status",
                table: "opportunities",
                columns: OpportunitiesByStatus);

            migrationBuilder.CreateIndex(
                name: "ix_opportunities_subject",
                table: "opportunities",
                columns: OpportunitiesBySubject);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "approval_tokens");

            migrationBuilder.DropTable(
                name: "kill_switch");

            migrationBuilder.DropTable(
                name: "ledger_entries");

            migrationBuilder.DropTable(
                name: "opportunities");
        }
    }
}
