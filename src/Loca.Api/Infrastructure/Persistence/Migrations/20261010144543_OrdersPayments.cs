using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Loca.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OrdersPayments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "orders");

            migrationBuilder.CreateTable(
                name: "inbox_messages",
                schema: "ops",
                columns: table => new
                {
                    source = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    event_id = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    payload_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    type = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    outcome = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_inbox_messages", x => new { x.source, x.event_id });
                });

            migrationBuilder.CreateTable(
                name: "orders",
                schema: "orders",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    reservation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_number = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    total_minor = table.Column<long>(type: "bigint", nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    paid_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cancel_reason = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_orders", x => x.id);
                    table.CheckConstraint("ck_orders_cancel_reason", "cancel_reason IS NULL OR cancel_reason IN ('Customer','SessionCancelled')");
                    table.CheckConstraint("ck_orders_currency", "currency = 'TRY'");
                    table.CheckConstraint("ck_orders_paid_at", "status <> 'Paid' OR paid_at IS NOT NULL");
                    table.CheckConstraint("ck_orders_status", "status IN ('PendingPayment','Paid','Failed','Expired','Cancelled')");
                    table.CheckConstraint("ck_orders_total", "total_minor >= 0");
                    table.ForeignKey(
                        name: "fk_orders_organizations_organization_id",
                        column: x => x.organization_id,
                        principalSchema: "identity",
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_orders_reservations_reservation_id",
                        column: x => x.reservation_id,
                        principalSchema: "inventory",
                        principalTable: "reservations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_orders_sessions_session_id",
                        column: x => x.session_id,
                        principalSchema: "catalog",
                        principalTable: "sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_orders_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "outbox_messages",
                schema: "ops",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    sequence = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    type = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    correlation_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    available_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    attempt_count = table.Column<int>(type: "integer", nullable: false),
                    lease_owner = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    lease_token = table.Column<Guid>(type: "uuid", nullable: true),
                    lease_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    dead_lettered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error_code = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbox_messages", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "reconciliation_runs",
                schema: "orders",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_type = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    target_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    requested_by = table.Column<Guid>(type: "uuid", nullable: true),
                    outcome = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    detail_code = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_reconciliation_runs", x => x.id);
                    table.CheckConstraint("ck_reconciliation_runs_outcome", "outcome IN ('Planned','Attempted','Succeeded','Failed')");
                    table.CheckConstraint("ck_reconciliation_runs_target", "target_type IN ('payment','refund')");
                });

            migrationBuilder.CreateTable(
                name: "payments",
                schema: "orders",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    merchant_reference = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    provider_payment_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    status = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    amount_minor = table.Column<long>(type: "bigint", nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    method_token = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    failure_kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    failure_code = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    last_provider_sequence = table.Column<int>(type: "integer", nullable: false),
                    attempt_count = table.Column<int>(type: "integer", nullable: false),
                    next_reconcile_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    lease_owner = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    lease_token = table.Column<Guid>(type: "uuid", nullable: true),
                    lease_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    late_success_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    review_reason = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_payments", x => x.id);
                    table.CheckConstraint("ck_payments_amount", "amount_minor >= 0");
                    table.CheckConstraint("ck_payments_currency", "currency = 'TRY'");
                    table.CheckConstraint("ck_payments_failure_consistency", "(status = 'Declined' AND failure_kind = 'Declined') OR (status = 'Failed' AND failure_kind = 'ProviderError') OR (status NOT IN ('Declined','Failed') AND failure_kind IS NULL)");
                    table.CheckConstraint("ck_payments_failure_kind", "failure_kind IS NULL OR failure_kind IN ('Declined','ProviderError')");
                    table.CheckConstraint("ck_payments_status", "status IN ('Initiated','Pending','Unknown','Succeeded','Declined','Failed')");
                    table.ForeignKey(
                        name: "fk_payments_orders_order_id",
                        column: x => x.order_id,
                        principalSchema: "orders",
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "refunds",
                schema: "orders",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    payment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    merchant_reference = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    provider_refund_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    reason = table.Column<string>(type: "character varying(28)", maxLength: 28, nullable: false),
                    status = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    amount_minor = table.Column<long>(type: "bigint", nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    attempt_count = table.Column<int>(type: "integer", nullable: false),
                    last_error_code = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_refunds", x => x.id);
                    table.CheckConstraint("ck_refunds_amount", "amount_minor > 0");
                    table.CheckConstraint("ck_refunds_currency", "currency = 'TRY'");
                    table.CheckConstraint("ck_refunds_reason", "reason IN ('CustomerCancellation','SessionCancellation','LatePaymentCompensation')");
                    table.CheckConstraint("ck_refunds_status", "status IN ('Pending','Succeeded','Failed')");
                    table.ForeignKey(
                        name: "fk_refunds_orders_order_id",
                        column: x => x.order_id,
                        principalSchema: "orders",
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_refunds_payments_payment_id",
                        column: x => x.payment_id,
                        principalSchema: "orders",
                        principalTable: "payments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_inbox_messages_received_at",
                schema: "ops",
                table: "inbox_messages",
                column: "received_at");

            migrationBuilder.CreateIndex(
                name: "ix_orders_order_number",
                schema: "orders",
                table: "orders",
                column: "order_number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_orders_organization_id",
                schema: "orders",
                table: "orders",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "ix_orders_session_id_status",
                schema: "orders",
                table: "orders",
                columns: new[] { "session_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_orders_user_id_created_at",
                schema: "orders",
                table: "orders",
                columns: new[] { "user_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ux_orders_reservation",
                schema: "orders",
                table: "orders",
                column: "reservation_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_dead_lettered_at",
                schema: "ops",
                table: "outbox_messages",
                column: "dead_lettered_at",
                filter: "dead_lettered_at IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_pending",
                schema: "ops",
                table: "outbox_messages",
                columns: new[] { "available_at", "sequence" },
                filter: "processed_at IS NULL AND dead_lettered_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_sequence",
                schema: "ops",
                table: "outbox_messages",
                column: "sequence",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_payments_merchant_reference",
                schema: "orders",
                table: "payments",
                column: "merchant_reference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_payments_order_id",
                schema: "orders",
                table: "payments",
                column: "order_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_payments_reconcile_due",
                schema: "orders",
                table: "payments",
                column: "next_reconcile_at",
                filter: "status IN ('Initiated','Pending','Unknown') AND next_reconcile_at IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_reconciliation_runs_organization_id_created_at",
                schema: "orders",
                table: "reconciliation_runs",
                columns: new[] { "organization_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_reconciliation_runs_target_type_target_id",
                schema: "orders",
                table: "reconciliation_runs",
                columns: new[] { "target_type", "target_id" });

            migrationBuilder.CreateIndex(
                name: "ix_refunds_merchant_reference",
                schema: "orders",
                table: "refunds",
                column: "merchant_reference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_refunds_order_id",
                schema: "orders",
                table: "refunds",
                column: "order_id");

            migrationBuilder.CreateIndex(
                name: "ix_refunds_status",
                schema: "orders",
                table: "refunds",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ux_refunds_payment",
                schema: "orders",
                table: "refunds",
                column: "payment_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "inbox_messages",
                schema: "ops");

            migrationBuilder.DropTable(
                name: "outbox_messages",
                schema: "ops");

            migrationBuilder.DropTable(
                name: "reconciliation_runs",
                schema: "orders");

            migrationBuilder.DropTable(
                name: "refunds",
                schema: "orders");

            migrationBuilder.DropTable(
                name: "payments",
                schema: "orders");

            migrationBuilder.DropTable(
                name: "orders",
                schema: "orders");
        }
    }
}
