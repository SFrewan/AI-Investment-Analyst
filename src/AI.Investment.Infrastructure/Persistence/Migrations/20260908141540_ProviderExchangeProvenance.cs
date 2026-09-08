using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AI.Investment.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ProviderExchangeProvenance : Migration
    {
        /// <summary>
        /// The natural key of an exchange: which run, and which position within its paging loop.
        /// </summary>
        /// <remarks>
        /// Hoisted out of the CreateIndex call because this repository builds with warnings as
        /// errors and CA1861 asks for exactly this. The columns are unchanged.
        /// </remarks>
        private static readonly string[] RunAndOrdinal = ["ingestion_run_id", "exchange_ordinal"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "provider_exchanges",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    ingestion_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    exchange_ordinal = table.Column<int>(type: "integer", nullable: false),
                    source_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    request_fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    endpoint_template = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    redacted_request_parameters = table.Column<string>(type: "jsonb", nullable: false),
                    requested_from_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    requested_to_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    http_status_code = table.Column<int>(type: "integer", nullable: true),
                    selected_response_headers = table.Column<string>(type: "jsonb", nullable: false),
                    retrieved_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    response_content_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    response_byte_length = table.Column<int>(type: "integer", nullable: true),
                    provider_correlation_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    recorded_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_provider_exchanges", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_provider_exchanges_ingestion_run_id",
                table: "provider_exchanges",
                column: "ingestion_run_id");

            migrationBuilder.CreateIndex(
                name: "ix_provider_exchanges_request_fingerprint",
                table: "provider_exchanges",
                column: "request_fingerprint");

            migrationBuilder.CreateIndex(
                name: "ix_provider_exchanges_response_content_hash",
                table: "provider_exchanges",
                column: "response_content_hash");

            migrationBuilder.CreateIndex(
                name: "ix_provider_exchanges_retrieved_at_utc",
                table: "provider_exchanges",
                column: "retrieved_at_utc");

            migrationBuilder.CreateIndex(
                name: "ux_provider_exchanges_run_ordinal",
                table: "provider_exchanges",
                columns: RunAndOrdinal,
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "provider_exchanges");
        }
    }
}
