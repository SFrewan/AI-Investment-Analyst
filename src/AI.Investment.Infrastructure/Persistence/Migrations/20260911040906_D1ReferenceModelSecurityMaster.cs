using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AI.Investment.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class D1ReferenceModelSecurityMaster : Migration
    {
        /// <summary>The (security, venue) pair that keys a listing. Hoisted for CA1861.</summary>
        private static readonly string[] ListingPairColumns = ["security_id", "venue_id"];

        /// <summary>The pairing plus the effective date - the point-in-time read path.</summary>
        private static readonly string[] PairingEffectiveColumns =
            ["security_id", "venue_id", "effective_date"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "securities",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_securities", x => x.id);
                    table.ForeignKey(
                        name: "FK_securities_companies_company_id",
                        column: x => x.company_id,
                        principalTable: "companies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "venues",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    country_code = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: true),
                    valid_from = table.Column<DateOnly>(type: "date", nullable: false),
                    valid_to = table.Column<DateOnly>(type: "date", nullable: true),
                    recorded_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_venues", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "listings",
                columns: table => new
                {
                    security_id = table.Column<Guid>(type: "uuid", nullable: false),
                    venue_id = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    opened_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_listings", x => new { x.security_id, x.venue_id });
                    table.ForeignKey(
                        name: "FK_listings_securities_security_id",
                        column: x => x.security_id,
                        principalTable: "securities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_listings_venues_venue_id",
                        column: x => x.venue_id,
                        principalTable: "venues",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "listing_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    security_id = table.Column<Guid>(type: "uuid", nullable: false),
                    venue_id = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    effective_date = table.Column<DateOnly>(type: "date", nullable: false),
                    reason_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    source_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    evidence_content_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    published_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    recorded_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_listing_events", x => x.id);
                    table.ForeignKey(
                        name: "FK_listing_events_listings_security_id_venue_id",
                        columns: x => new { x.security_id, x.venue_id },
                        principalTable: "listings",
                        principalColumns: ListingPairColumns,
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_listing_events_pairing_effective",
                table: "listing_events",
                columns: PairingEffectiveColumns);

            migrationBuilder.CreateIndex(
                name: "ix_listing_events_published_at_utc",
                table: "listing_events",
                column: "published_at_utc");

            migrationBuilder.CreateIndex(
                name: "ix_listings_venue_id",
                table: "listings",
                column: "venue_id");

            migrationBuilder.CreateIndex(
                name: "ix_securities_company_id",
                table: "securities",
                column: "company_id");

            migrationBuilder.CreateIndex(
                name: "ix_venues_name",
                table: "venues",
                column: "name");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "listing_events");

            migrationBuilder.DropTable(
                name: "listings");

            migrationBuilder.DropTable(
                name: "securities");

            migrationBuilder.DropTable(
                name: "venues");
        }
    }
}
