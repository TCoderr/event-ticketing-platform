using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Loca.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Catalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "catalog");

            migrationBuilder.EnsureSchema(
                name: "inventory");

            migrationBuilder.CreateTable(
                name: "events",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    slug = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    category = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    moderation_blocked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    is_demo = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_events", x => x.id);
                    table.CheckConstraint("ck_events_category", "category IN ('Concert','Theatre','Festival','Sports','Standup','Conference','Other')");
                    table.CheckConstraint("ck_events_status", "status IN ('Draft','Published','Cancelled')");
                    table.ForeignKey(
                        name: "fk_events_organizations_organization_id",
                        column: x => x.organization_id,
                        principalSchema: "identity",
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "venues",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    city = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    address = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    timezone = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_venues", x => x.id);
                    table.ForeignKey(
                        name: "fk_venues_organizations_organization_id",
                        column: x => x.organization_id,
                        principalSchema: "identity",
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "seat_layouts",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    venue_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    seat_count = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_seat_layouts", x => x.id);
                    table.ForeignKey(
                        name: "fk_seat_layouts_venues_venue_id",
                        column: x => x.venue_id,
                        principalSchema: "catalog",
                        principalTable: "venues",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "layout_seats",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    layout_id = table.Column<Guid>(type: "uuid", nullable: false),
                    section = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    row_label = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: false),
                    seat_number = table.Column<int>(type: "integer", nullable: false),
                    x = table.Column<int>(type: "integer", nullable: false),
                    y = table.Column<int>(type: "integer", nullable: false),
                    tier_code = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_layout_seats", x => x.id);
                    table.ForeignKey(
                        name: "fk_layout_seats_seat_layouts_layout_id",
                        column: x => x.layout_id,
                        principalSchema: "catalog",
                        principalTable: "seat_layouts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sessions",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    venue_id = table.Column<Guid>(type: "uuid", nullable: false),
                    city = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    timezone = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    starts_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    sales_start_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    sales_end_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    sales_mode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    hold_ttl_seconds = table.Column<int>(type: "integer", nullable: false),
                    layout_id = table.Column<Guid>(type: "uuid", nullable: true),
                    demo_simulator_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    cancelled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sessions", x => x.id);
                    table.CheckConstraint("ck_sessions_hold_ttl", "hold_ttl_seconds BETWEEN 5 AND 1800");
                    table.CheckConstraint("ck_sessions_sales_mode", "sales_mode IN ('ReservedSeating','GeneralAdmission')");
                    table.CheckConstraint("ck_sessions_sales_window", "sales_start_at < sales_end_at AND sales_end_at <= starts_at");
                    table.CheckConstraint("ck_sessions_status", "status IN ('Draft','Published','Cancelled')");
                    table.ForeignKey(
                        name: "fk_sessions_events_event_id",
                        column: x => x.event_id,
                        principalSchema: "catalog",
                        principalTable: "events",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sessions_organizations_organization_id",
                        column: x => x.organization_id,
                        principalSchema: "identity",
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sessions_seat_layouts_layout_id",
                        column: x => x.layout_id,
                        principalSchema: "catalog",
                        principalTable: "seat_layouts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sessions_venues_venue_id",
                        column: x => x.venue_id,
                        principalSchema: "catalog",
                        principalTable: "venues",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ga_categories",
                schema: "inventory",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    price_minor = table.Column<long>(type: "bigint", nullable: false),
                    capacity = table.Column<int>(type: "integer", nullable: false),
                    held_count = table.Column<int>(type: "integer", nullable: false),
                    sold_count = table.Column<int>(type: "integer", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ga_categories", x => x.id);
                    table.CheckConstraint("ck_ga_categories_capacity", "capacity >= 1");
                    table.CheckConstraint("ck_ga_categories_counts", "held_count >= 0 AND sold_count >= 0 AND held_count + sold_count <= capacity");
                    table.CheckConstraint("ck_ga_categories_price", "price_minor >= 0");
                    table.ForeignKey(
                        name: "fk_ga_categories_sessions_session_id",
                        column: x => x.session_id,
                        principalSchema: "catalog",
                        principalTable: "sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "session_seats",
                schema: "inventory",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    section = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    row_label = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: false),
                    seat_number = table.Column<int>(type: "integer", nullable: false),
                    x = table.Column<int>(type: "integer", nullable: false),
                    y = table.Column<int>(type: "integer", nullable: false),
                    tier_code = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    price_minor = table.Column<long>(type: "bigint", nullable: false),
                    status = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    hold_reservation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    held_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_session_seats", x => x.id);
                    table.CheckConstraint("ck_session_seats_ownership", "(status = 'Available' AND hold_reservation_id IS NULL AND held_until IS NULL) OR (status = 'Held' AND hold_reservation_id IS NOT NULL AND held_until IS NOT NULL) OR (status = 'Sold' AND hold_reservation_id IS NOT NULL AND held_until IS NULL)");
                    table.CheckConstraint("ck_session_seats_price", "price_minor >= 0");
                    table.CheckConstraint("ck_session_seats_status", "status IN ('Available','Held','Sold')");
                    table.ForeignKey(
                        name: "fk_session_seats_sessions_session_id",
                        column: x => x.session_id,
                        principalSchema: "catalog",
                        principalTable: "sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_events_organization_id_created_at",
                schema: "catalog",
                table: "events",
                columns: new[] { "organization_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_events_slug",
                schema: "catalog",
                table: "events",
                column: "slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_ga_categories_session_id_name",
                schema: "inventory",
                table: "ga_categories",
                columns: new[] { "session_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_layout_seats_layout_id_section_row_label_seat_number",
                schema: "catalog",
                table: "layout_seats",
                columns: new[] { "layout_id", "section", "row_label", "seat_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_seat_layouts_venue_id_version",
                schema: "catalog",
                table: "seat_layouts",
                columns: new[] { "venue_id", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_session_seats_hold_reservation_id",
                schema: "inventory",
                table: "session_seats",
                column: "hold_reservation_id",
                filter: "hold_reservation_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_session_seats_session_id_section_row_label_seat_number",
                schema: "inventory",
                table: "session_seats",
                columns: new[] { "session_id", "section", "row_label", "seat_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_sessions_event_id",
                schema: "catalog",
                table: "sessions",
                column: "event_id");

            migrationBuilder.CreateIndex(
                name: "ix_sessions_layout_id",
                schema: "catalog",
                table: "sessions",
                column: "layout_id");

            migrationBuilder.CreateIndex(
                name: "ix_sessions_organization_id",
                schema: "catalog",
                table: "sessions",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "ix_sessions_public_discovery",
                schema: "catalog",
                table: "sessions",
                columns: new[] { "starts_at", "id" },
                filter: "status = 'Published'");

            migrationBuilder.CreateIndex(
                name: "ix_sessions_venue_id",
                schema: "catalog",
                table: "sessions",
                column: "venue_id");

            migrationBuilder.CreateIndex(
                name: "ix_venues_organization_id",
                schema: "catalog",
                table: "venues",
                column: "organization_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ga_categories",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "layout_seats",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "session_seats",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "sessions",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "events",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "seat_layouts",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "venues",
                schema: "catalog");
        }
    }
}
