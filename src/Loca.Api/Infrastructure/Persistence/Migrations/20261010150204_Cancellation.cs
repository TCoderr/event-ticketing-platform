using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Loca.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Cancellation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "session_cancellation_jobs",
                schema: "orders",
                columns: table => new
                {
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    requested_by = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    cursor_reservation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    processed_count = table.Column<int>(type: "integer", nullable: false),
                    refund_count = table.Column<int>(type: "integer", nullable: false),
                    lease_owner = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    lease_token = table.Column<Guid>(type: "uuid", nullable: true),
                    lease_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_session_cancellation_jobs", x => x.session_id);
                    table.CheckConstraint("ck_session_cancellation_jobs_status", "status IN ('Pending','Completed')");
                    table.ForeignKey(
                        name: "fk_session_cancellation_jobs_sessions_session_id",
                        column: x => x.session_id,
                        principalSchema: "catalog",
                        principalTable: "sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_session_cancellation_jobs_status",
                schema: "orders",
                table: "session_cancellation_jobs",
                column: "status",
                filter: "status = 'Pending'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "session_cancellation_jobs",
                schema: "orders");
        }
    }
}
