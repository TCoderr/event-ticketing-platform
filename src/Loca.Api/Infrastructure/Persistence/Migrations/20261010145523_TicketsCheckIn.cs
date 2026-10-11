using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Loca.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TicketsCheckIn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "tickets");

            migrationBuilder.AlterColumn<string>(
                name: "outcome",
                schema: "ops",
                table: "inbox_messages",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(24)",
                oldMaxLength: 24);

            migrationBuilder.CreateTable(
                name: "gate_assignments",
                schema: "tickets",
                columns: table => new
                {
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    assigned_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_gate_assignments", x => new { x.session_id, x.user_id });
                    table.ForeignKey(
                        name: "fk_gate_assignments_sessions_session_id",
                        column: x => x.session_id,
                        principalSchema: "catalog",
                        principalTable: "sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_gate_assignments_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "scan_log",
                schema: "tickets",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: true),
                    staff_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    result = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    gate = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_scan_log", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "tickets",
                schema: "tickets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reservation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reservation_line_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ordinal = table.Column<int>(type: "integer", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_seat_id = table.Column<Guid>(type: "uuid", nullable: true),
                    ga_category_id = table.Column<Guid>(type: "uuid", nullable: true),
                    label = table.Column<string>(type: "character varying(140)", maxLength: 140, nullable: false),
                    status = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    token_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    token_protected = table.Column<string>(type: "character varying(600)", maxLength: 600, nullable: false),
                    issued_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    checked_in_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    checked_in_by = table.Column<Guid>(type: "uuid", nullable: true),
                    checked_in_gate = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    voided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    void_reason = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tickets", x => x.id);
                    table.CheckConstraint("ck_tickets_checked_in", "status <> 'CheckedIn' OR checked_in_at IS NOT NULL");
                    table.CheckConstraint("ck_tickets_ordinal", "ordinal BETWEEN 1 AND 6");
                    table.CheckConstraint("ck_tickets_status", "status IN ('Valid','CheckedIn','Cancelled')");
                    table.CheckConstraint("ck_tickets_valid_unused", "status <> 'Valid' OR checked_in_at IS NULL");
                    table.CheckConstraint("ck_tickets_voided", "(status = 'Cancelled') = (voided_at IS NOT NULL)");
                    table.ForeignKey(
                        name: "fk_tickets_orders_order_id",
                        column: x => x.order_id,
                        principalSchema: "orders",
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_tickets_reservation_lines_reservation_line_id",
                        column: x => x.reservation_line_id,
                        principalSchema: "inventory",
                        principalTable: "reservation_lines",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_tickets_reservations_reservation_id",
                        column: x => x.reservation_id,
                        principalSchema: "inventory",
                        principalTable: "reservations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_tickets_sessions_session_id",
                        column: x => x.session_id,
                        principalSchema: "catalog",
                        principalTable: "sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_tickets_users_owner_user_id",
                        column: x => x.owner_user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_gate_assignments_user_id",
                schema: "tickets",
                table: "gate_assignments",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_scan_log_session_id_created_at",
                schema: "tickets",
                table: "scan_log",
                columns: new[] { "session_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_tickets_order_id",
                schema: "tickets",
                table: "tickets",
                column: "order_id");

            migrationBuilder.CreateIndex(
                name: "ix_tickets_owner_user_id_issued_at",
                schema: "tickets",
                table: "tickets",
                columns: new[] { "owner_user_id", "issued_at" });

            migrationBuilder.CreateIndex(
                name: "ix_tickets_reservation_id",
                schema: "tickets",
                table: "tickets",
                column: "reservation_id");

            migrationBuilder.CreateIndex(
                name: "ix_tickets_reservation_line_id_ordinal",
                schema: "tickets",
                table: "tickets",
                columns: new[] { "reservation_line_id", "ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_tickets_session_id_status",
                schema: "tickets",
                table: "tickets",
                columns: new[] { "session_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_tickets_token_hash",
                schema: "tickets",
                table: "tickets",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_tickets_one_live_ticket_per_seat",
                schema: "tickets",
                table: "tickets",
                column: "session_seat_id",
                unique: true,
                filter: "session_seat_id IS NOT NULL AND status IN ('Valid','CheckedIn')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "gate_assignments",
                schema: "tickets");

            migrationBuilder.DropTable(
                name: "scan_log",
                schema: "tickets");

            migrationBuilder.DropTable(
                name: "tickets",
                schema: "tickets");

            migrationBuilder.AlterColumn<string>(
                name: "outcome",
                schema: "ops",
                table: "inbox_messages",
                type: "character varying(24)",
                maxLength: 24,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(40)",
                oldMaxLength: 40);
        }
    }
}
