using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AI.Investment.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class G2SecurityIdentifierPersistence : Migration
    {
        // Hoisted out of the CreateIndex call because CA1861 refuses a constant array argument
        // inside a method call, and this repository builds with warnings as errors. Every prior
        // migration carrying a multi-column index does the same.
        private static readonly string[] KindAndValue = ["kind", "value"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "security_identifiers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<int>(type: "integer", nullable: false),
                    value = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    valid_from = table.Column<DateOnly>(type: "date", nullable: false),
                    valid_to = table.Column<DateOnly>(type: "date", nullable: true),
                    source_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    security_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_security_identifiers", x => x.id);
                    table.ForeignKey(
                        name: "FK_security_identifiers_securities_security_id",
                        column: x => x.security_id,
                        principalTable: "securities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_security_identifiers_security_id",
                table: "security_identifiers",
                column: "security_id");

            migrationBuilder.CreateIndex(
                name: "ux_security_identifiers_kind_value",
                table: "security_identifiers",
                columns: KindAndValue,
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "security_identifiers");
        }
    }
}
