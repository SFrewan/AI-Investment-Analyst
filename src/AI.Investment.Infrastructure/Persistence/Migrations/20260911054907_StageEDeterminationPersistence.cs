using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AI.Investment.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class StageEDeterminationPersistence : Migration
    {
        /// <summary>The reproducibility key: rule, version, as-of and the ordered-input digest. Hoisted for CA1861.</summary>
        private static readonly string[] ReproducibilityKeyColumns =
            ["rule_id", "rule_version", "as_of_utc", "inputs_hash"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "determinations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    rule_id = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    rule_version = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    output_canonical = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    output_content_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    as_of_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    confidence = table.Column<decimal>(type: "numeric", nullable: true),
                    recorded_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    inputs_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    inputs = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_determinations", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_determinations_as_of_utc",
                table: "determinations",
                column: "as_of_utc");

            migrationBuilder.CreateIndex(
                name: "ix_determinations_reproducibility",
                table: "determinations",
                columns: ReproducibilityKeyColumns,
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_determinations_rule_id",
                table: "determinations",
                column: "rule_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "determinations");
        }
    }
}
