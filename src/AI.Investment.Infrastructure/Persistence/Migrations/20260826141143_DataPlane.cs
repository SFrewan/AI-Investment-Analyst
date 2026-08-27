using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AI.Investment.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class DataPlane : Migration
{
    private static readonly string[] ObservationsSubjectIndexColumns =
{
    "subject_kind",
    "subject_identifier"
};
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "data_sources",
            columns: table => new
            {
                id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                type = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                authority = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                region = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                cadence_kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                cadence_interval = table.Column<TimeSpan>(type: "interval", nullable: true),
                licence_storage_allowed = table.Column<bool>(type: "boolean", nullable: false),
                licence_redistribution_allowed = table.Column<bool>(type: "boolean", nullable: false),
                licence_processing_allowed = table.Column<bool>(type: "boolean", nullable: false),
                licence_attribution_required = table.Column<bool>(type: "boolean", nullable: false),
                licence_retention = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                licence_notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                verification_can_confirm_alone = table.Column<bool>(type: "boolean", nullable: false),
                verification_required_sources = table.Column<int>(type: "integer", nullable: false),
                reliability = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                is_active = table.Column<bool>(type: "boolean", nullable: false),
                description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                registered_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                categories = table.Column<string>(type: "jsonb", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_data_sources", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "ingestion_runs",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                source_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                category = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                region = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                subject_kind = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                subject_identifier = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                window_start_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                window_end_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                correlation_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                requested_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                started_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                completed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                outcome = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                refusal_rule_id = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                request_fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                artifacts = table.Column<string>(type: "jsonb", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ingestion_runs", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "observations",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                subject_kind = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                subject_identifier = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                attribute = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                value_kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                value = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                claim_kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                source_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                source_record_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                source_url = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                as_of_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                published_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                retrieved_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                confidence = table.Column<decimal>(type: "numeric(5,4)", precision: 5, scale: 4, nullable: true),
                caveats = table.Column<string>(type: "jsonb", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_observations", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "quarantined_payloads",
            columns: table => new
            {
                content_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                source_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                category = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                rule_id = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                quarantined_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_quarantined_payloads", x => x.content_hash);
            });

        migrationBuilder.CreateTable(
            name: "unreplayable_evidence",
            columns: table => new
            {
                content_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                source_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                rule_id = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                marked_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_unreplayable_evidence", x => x.content_hash);
            });

        migrationBuilder.CreateIndex(
            name: "ix_data_sources_is_active",
            table: "data_sources",
            column: "is_active");

        migrationBuilder.CreateIndex(
            name: "ix_data_sources_region",
            table: "data_sources",
            column: "region");

        migrationBuilder.CreateIndex(
            name: "ix_ingestion_runs_outcome",
            table: "ingestion_runs",
            column: "outcome");

        migrationBuilder.CreateIndex(
            name: "ix_ingestion_runs_request_fingerprint",
            table: "ingestion_runs",
            column: "request_fingerprint");

        migrationBuilder.CreateIndex(
            name: "ix_ingestion_runs_started_at_utc",
            table: "ingestion_runs",
            column: "started_at_utc");

        migrationBuilder.CreateIndex(
            name: "ix_observations_attribute",
            table: "observations",
            column: "attribute");

        migrationBuilder.CreateIndex(
            name: "ix_observations_published_at_utc",
            table: "observations",
            column: "published_at_utc");

        migrationBuilder.CreateIndex(
            name: "ix_observations_subject",
            table: "observations",
           columns: ObservationsSubjectIndexColumns);

        migrationBuilder.CreateIndex(
            name: "ix_quarantined_payloads_quarantined_at_utc",
            table: "quarantined_payloads",
            column: "quarantined_at_utc");

        migrationBuilder.CreateIndex(
            name: "ix_quarantined_payloads_rule_id",
            table: "quarantined_payloads",
            column: "rule_id");

        migrationBuilder.CreateIndex(
            name: "ix_quarantined_payloads_source_id",
            table: "quarantined_payloads",
            column: "source_id");

        migrationBuilder.CreateIndex(
            name: "ix_unreplayable_evidence_marked_at_utc",
            table: "unreplayable_evidence",
            column: "marked_at_utc");

        migrationBuilder.CreateIndex(
            name: "ix_unreplayable_evidence_source_id",
            table: "unreplayable_evidence",
            column: "source_id");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "data_sources");

        migrationBuilder.DropTable(
            name: "ingestion_runs");

        migrationBuilder.DropTable(
            name: "observations");

        migrationBuilder.DropTable(
            name: "quarantined_payloads");

        migrationBuilder.DropTable(
            name: "unreplayable_evidence");
    }
}
