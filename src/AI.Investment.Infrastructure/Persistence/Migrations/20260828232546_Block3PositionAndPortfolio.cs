using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AI.Investment.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Block3PositionAndPortfolio : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "position_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    instrument = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    change = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    price = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    price_currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    fees = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    fees_currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    venue_reference = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    opportunity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurred_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_position_events", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_position_events_instrument",
                table: "position_events",
                column: "instrument");

            migrationBuilder.CreateIndex(
                name: "ix_position_events_occurred_at_utc",
                table: "position_events",
                column: "occurred_at_utc");

            migrationBuilder.CreateIndex(
                name: "ux_position_events_venue_reference",
                table: "position_events",
                column: "venue_reference",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "position_events");
        }
    }
}
