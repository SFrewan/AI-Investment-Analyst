using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AI.Investment.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class D2UniverseAndMembership : Migration
    {
        /// <summary>The (universe, security) pair a membership is unique on. Hoisted for CA1861.</summary>
        private static readonly string[] UniverseSecurityColumns = ["universe_id", "security_id"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "universes",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    window_from = table.Column<DateOnly>(type: "date", nullable: false),
                    window_to = table.Column<DateOnly>(type: "date", nullable: false),
                    sealed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    manifest_content_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    population_definition = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    membership_rule = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    recorded_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    cohort_cut_dates = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_universes", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "universe_memberships",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    universe_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    security_id = table.Column<Guid>(type: "uuid", nullable: false),
                    recorded_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    cohort_cuts = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_universe_memberships", x => x.id);
                    table.ForeignKey(
                        name: "FK_universe_memberships_securities_security_id",
                        column: x => x.security_id,
                        principalTable: "securities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_universe_memberships_universes_universe_id",
                        column: x => x.universe_id,
                        principalTable: "universes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_universe_memberships_security_id",
                table: "universe_memberships",
                column: "security_id");

            migrationBuilder.CreateIndex(
                name: "ix_universe_memberships_universe_security",
                table: "universe_memberships",
                columns: UniverseSecurityColumns,
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_universes_sealed_at_utc",
                table: "universes",
                column: "sealed_at_utc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "universe_memberships");

            migrationBuilder.DropTable(
                name: "universes");
        }
    }
}
