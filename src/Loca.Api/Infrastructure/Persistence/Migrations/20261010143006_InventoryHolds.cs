using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Loca.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InventoryHolds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "idempotency_records",
                schema: "ops",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    operation = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    hash_version = table.Column<int>(type: "integer", nullable: false),
                    request_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    response_status = table.Column<int>(type: "integer", nullable: true),
                    response_content_type = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    response_location = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    response_body = table.Column<byte[]>(type: "bytea", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_idempotency_records", x => new { x.user_id, x.operation, x.idempotency_key });
                });

            migrationBuilder.CreateTable(
                name: "reservations",
                schema: "inventory",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ticket_count = table.Column<int>(type: "integer", nullable: false),
                    total_minor = table.Column<long>(type: "bigint", nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    close_reason = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_reservations", x => x.id);
                    table.CheckConstraint("ck_reservations_closed", "(status = 'Active') = (closed_at IS NULL)");
                    table.CheckConstraint("ck_reservations_currency", "currency = 'TRY'");
                    table.CheckConstraint("ck_reservations_status", "status IN ('Active','Consumed','Expired','Released')");
                    table.CheckConstraint("ck_reservations_ticket_count", "ticket_count BETWEEN 1 AND 6");
                    table.CheckConstraint("ck_reservations_total", "total_minor >= 0");
                    table.ForeignKey(
                        name: "fk_reservations_sessions_session_id",
                        column: x => x.session_id,
                        principalSchema: "catalog",
                        principalTable: "sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_reservations_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "reservation_lines",
                schema: "inventory",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    reservation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_no = table.Column<int>(type: "integer", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    session_seat_id = table.Column<Guid>(type: "uuid", nullable: true),
                    ga_category_id = table.Column<Guid>(type: "uuid", nullable: true),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    unit_price_minor = table.Column<long>(type: "bigint", nullable: false),
                    label = table.Column<string>(type: "character varying(140)", maxLength: 140, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_reservation_lines", x => x.id);
                    table.CheckConstraint("ck_reservation_lines_kind", "(kind = 'Seat' AND session_seat_id IS NOT NULL AND ga_category_id IS NULL AND quantity = 1) OR (kind = 'GeneralAdmission' AND ga_category_id IS NOT NULL AND session_seat_id IS NULL)");
                    table.CheckConstraint("ck_reservation_lines_price", "unit_price_minor >= 0");
                    table.CheckConstraint("ck_reservation_lines_quantity", "quantity BETWEEN 1 AND 6");
                    table.ForeignKey(
                        name: "fk_reservation_lines_ga_categories_ga_category_id",
                        column: x => x.ga_category_id,
                        principalSchema: "inventory",
                        principalTable: "ga_categories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_reservation_lines_reservations_reservation_id",
                        column: x => x.reservation_id,
                        principalSchema: "inventory",
                        principalTable: "reservations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_reservation_lines_session_seats_session_seat_id",
                        column: x => x.session_seat_id,
                        principalSchema: "inventory",
                        principalTable: "session_seats",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_idempotency_records_expires_at",
                schema: "ops",
                table: "idempotency_records",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_reservation_lines_ga_category_id",
                schema: "inventory",
                table: "reservation_lines",
                column: "ga_category_id");

            migrationBuilder.CreateIndex(
                name: "ix_reservation_lines_reservation_id_line_no",
                schema: "inventory",
                table: "reservation_lines",
                columns: new[] { "reservation_id", "line_no" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_reservation_lines_session_seat_id",
                schema: "inventory",
                table: "reservation_lines",
                column: "session_seat_id");

            migrationBuilder.CreateIndex(
                name: "ix_reservations_active_expiry",
                schema: "inventory",
                table: "reservations",
                columns: new[] { "expires_at", "id" },
                filter: "status = 'Active'");

            migrationBuilder.CreateIndex(
                name: "ix_reservations_session_id",
                schema: "inventory",
                table: "reservations",
                column: "session_id");

            migrationBuilder.CreateIndex(
                name: "ux_reservations_one_active_per_user_session",
                schema: "inventory",
                table: "reservations",
                columns: new[] { "user_id", "session_id" },
                unique: true,
                filter: "status = 'Active'");

            migrationBuilder.AddForeignKey(
                name: "fk_session_seats_reservations_hold_reservation_id",
                schema: "inventory",
                table: "session_seats",
                column: "hold_reservation_id",
                principalSchema: "inventory",
                principalTable: "reservations",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_session_seats_reservations_hold_reservation_id",
                schema: "inventory",
                table: "session_seats");

            migrationBuilder.DropTable(
                name: "idempotency_records",
                schema: "ops");

            migrationBuilder.DropTable(
                name: "reservation_lines",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "reservations",
                schema: "inventory");
        }
    }
}
