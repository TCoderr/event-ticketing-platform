using Loca.Api.Modules.Catalog;
using Loca.Api.Modules.Identity;
using Loca.Domain;
using Microsoft.EntityFrameworkCore;

namespace Loca.Api.Modules.Inventory;

/// <summary>
/// Hold. Sipariş kümesinin (sipariş, ödeme, iade, bilet) kilit köküdür: yaşam döngüsünü değiştiren her
/// işlem önce bu satırı FOR UPDATE ile kilitler.
/// </summary>
public sealed class Reservation
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public Guid UserId { get; set; }
    public ReservationStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public int TicketCount { get; set; }
    public long TotalMinor { get; set; }
    public string Currency { get; set; } = Money.SupportedCurrency;
    public DateTimeOffset? ClosedAt { get; set; }
    public string? CloseReason { get; set; }
    public List<ReservationLine> Lines { get; set; } = [];
}

/// <summary>Değişmez satır snapshot'ı: fiyat ve etiket hold anında sabitlenir. Çalışma zamanı rolü UPDATE/DELETE yapamaz.</summary>
public sealed class ReservationLine
{
    public Guid Id { get; set; }
    public Guid ReservationId { get; set; }
    public int LineNo { get; set; }
    public ReservationLineKind Kind { get; set; }
    public Guid? SessionSeatId { get; set; }
    public Guid? GaCategoryId { get; set; }
    public int Quantity { get; set; }
    public long UnitPriceMinor { get; set; }
    public string Label { get; set; } = "";
}

internal static class ReservationModel
{
    public const string ActiveHoldPerUserIndex = "ux_reservations_one_active_per_user_session";

    public static void Configure(ModelBuilder b)
    {
        b.Entity<Reservation>(e =>
        {
            e.ToTable("reservations", InventoryModel.Schema, t =>
            {
                t.HasCheckConstraint("ck_reservations_status", "status IN ('Active','Consumed','Expired','Released')");
                t.HasCheckConstraint("ck_reservations_ticket_count", "ticket_count BETWEEN 1 AND 6");
                t.HasCheckConstraint("ck_reservations_currency", "currency = 'TRY'");
                t.HasCheckConstraint("ck_reservations_total", "total_minor >= 0");
                t.HasCheckConstraint("ck_reservations_closed", "(status = 'Active') = (closed_at IS NULL)");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(12);
            e.Property(x => x.Currency).HasMaxLength(3).IsFixedLength();
            e.Property(x => x.CloseReason).HasMaxLength(32);
            e.HasOne<Session>().WithMany().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Lines).WithOne().HasForeignKey(l => l.ReservationId).OnDelete(DeleteBehavior.Restrict);
            // Kullanıcı başına seans başına tek aktif hold.
            e.HasIndex(x => new { x.UserId, x.SessionId }).IsUnique().HasFilter("status = 'Active'").HasDatabaseName(ActiveHoldPerUserIndex);
            e.HasIndex(x => new { x.ExpiresAt, x.Id }).HasFilter("status = 'Active'").HasDatabaseName("ix_reservations_active_expiry");
            e.HasIndex(x => x.SessionId);
        });

        b.Entity<ReservationLine>(e =>
        {
            e.ToTable("reservation_lines", InventoryModel.Schema, t =>
            {
                t.HasCheckConstraint("ck_reservation_lines_kind",
                    "(kind = 'Seat' AND session_seat_id IS NOT NULL AND ga_category_id IS NULL AND quantity = 1) OR " +
                    "(kind = 'GeneralAdmission' AND ga_category_id IS NOT NULL AND session_seat_id IS NULL)");
                t.HasCheckConstraint("ck_reservation_lines_quantity", "quantity BETWEEN 1 AND 6");
                t.HasCheckConstraint("ck_reservation_lines_price", "unit_price_minor >= 0");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Label).HasMaxLength(140);
            e.HasOne<SessionSeat>().WithMany().HasForeignKey(x => x.SessionSeatId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<GaCategory>().WithMany().HasForeignKey(x => x.GaCategoryId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.ReservationId, x.LineNo }).IsUnique();
        });

        // Koltuğun sahibi var olan bir rezervasyon olmalıdır.
        b.Entity<SessionSeat>().HasOne<Reservation>().WithMany().HasForeignKey(s => s.HoldReservationId).OnDelete(DeleteBehavior.Restrict);
    }
}
