using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AI.Investment.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase8BoundedAutonomy : Migration
    {
        // Hoisted out of the index calls below. The generated code passes these inline, which is a
        // constant array argument on every call and fails CA1861 under warnings-as-errors. The column
        // lists are unchanged; only where they are written has moved.
        private static readonly string[] LiveVenueByVenue = ["venue_id", "environment"];

        private static readonly string[] PromotionWarrantLookup =
            ["capability", "environment", "expires_at_utc"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "promotion_warrant_id",
                table: "autonomy_grants",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "live_venue_authorizations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    venue_id = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    environment = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    promotion_warrant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    authorised_by = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    counter_signed_by = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    justification = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    exposure_ceiling = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    exposure_ceiling_currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    authorised_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    expires_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    withdrawn_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    withdrawal_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_live_venue_authorizations", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "promotion_warrants",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    capability = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    action_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    environment = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    max_mode = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    max_risk_tier = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    max_exposure = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    max_exposure_currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    validation_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    benchmark_fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    issued_by = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    justification = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    issued_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    expires_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    revoked_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    revocation_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_promotion_warrants", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ux_live_venue_authorizations_venue",
                table: "live_venue_authorizations",
                columns: LiveVenueByVenue,
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_promotion_warrants_lookup",
                table: "promotion_warrants",
                columns: PromotionWarrantLookup);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "live_venue_authorizations");

            migrationBuilder.DropTable(
                name: "promotion_warrants");

            migrationBuilder.DropColumn(
                name: "promotion_warrant_id",
                table: "autonomy_grants");
        }
    }
}
