using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Loca.Api.PspSimulator.Migrations
{
    /// <inheritdoc />
    public partial class InitialSimulator : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "pspsim");

            migrationBuilder.CreateTable(
                name: "charges",
                schema: "pspsim",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    merchant_reference = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    amount_minor = table.Column<long>(type: "bigint", nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    method_token = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    decline_code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    pending_outcome = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    sequence = table.Column<int>(type: "integer", nullable: false),
                    create_attempts = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    resolve_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_charges", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "refunds",
                schema: "pspsim",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    merchant_reference = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    charge_id = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    amount_minor = table.Column<long>(type: "bigint", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    failure_code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    create_attempts = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_refunds", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "webhook_deliveries",
                schema: "pspsim",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    ordinal = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    event_id = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    body = table.Column<byte[]>(type: "bytea", nullable: false),
                    deliver_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    delivered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_status = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_webhook_deliveries", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_charges_merchant_reference",
                schema: "pspsim",
                table: "charges",
                column: "merchant_reference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_refunds_charge_id",
                schema: "pspsim",
                table: "refunds",
                column: "charge_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_refunds_merchant_reference",
                schema: "pspsim",
                table: "refunds",
                column: "merchant_reference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_webhook_deliveries_deliver_at_ordinal",
                schema: "pspsim",
                table: "webhook_deliveries",
                columns: new[] { "deliver_at", "ordinal" },
                filter: "delivered_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "charges",
                schema: "pspsim");

            migrationBuilder.DropTable(
                name: "refunds",
                schema: "pspsim");

            migrationBuilder.DropTable(
                name: "webhook_deliveries",
                schema: "pspsim");
        }
    }
}
