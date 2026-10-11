using Loca.Api.Modules.Catalog;
using Loca.Domain;
using Microsoft.EntityFrameworkCore;

namespace Loca.Api.Modules.Inventory;

/// <summary>
/// Seansa özel koltuk satırı: plan sürümünden alınmış snapshot + envanter durumu.
/// Sahiplik tek sütundur (<see cref="HoldReservationId"/>); bir koltuğun aynı anda iki sahibi olamaz.
/// </summary>
public sealed class SessionSeat
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public string Section { get; set; } = "";
    public string RowLabel { get; set; } = "";
    public int SeatNumber { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public string TierCode { get; set; } = "";
    public long PriceMinor { get; set; }
    public SeatStatus Status { get; set; }
    public Guid? HoldReservationId { get; set; }
    public DateTimeOffset? HeldUntil { get; set; }

    public string Label => $"{Section} / {RowLabel} / {SeatNumber}";
}

/// <summary>Genel giriş bilet kategorisi. Sayaçlar CHECK kısıtıyla kapasiteyi aşamaz.</summary>
public sealed class GaCategory
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public string Name { get; set; } = "";
    public long PriceMinor { get; set; }
    public int Capacity { get; set; }
    public int HeldCount { get; set; }
    public int SoldCount { get; set; }
    public int SortOrder { get; set; }
}

internal static class InventoryModel
{
    public const string Schema = "inventory";

    public static void ConfigureStock(ModelBuilder b)
    {
        b.Entity<SessionSeat>(e =>
        {
            e.ToTable("session_seats", Schema, t =>
            {
                t.HasCheckConstraint("ck_session_seats_status", "status IN ('Available','Held','Sold')");
                t.HasCheckConstraint("ck_session_seats_price", "price_minor >= 0");
                t.HasCheckConstraint("ck_session_seats_ownership",
                    "(status = 'Available' AND hold_reservation_id IS NULL AND held_until IS NULL) OR " +
                    "(status = 'Held' AND hold_reservation_id IS NOT NULL AND held_until IS NOT NULL) OR " +
                    "(status = 'Sold' AND hold_reservation_id IS NOT NULL AND held_until IS NULL)");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Section).HasMaxLength(40);
            e.Property(x => x.RowLabel).HasMaxLength(4);
            e.Property(x => x.TierCode).HasMaxLength(8);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(12);
            e.Ignore(x => x.Label);
            e.HasOne<Session>().WithMany().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.SessionId, x.Section, x.RowLabel, x.SeatNumber }).IsUnique();
            e.HasIndex(x => x.HoldReservationId).HasFilter("hold_reservation_id IS NOT NULL");
        });

        b.Entity<GaCategory>(e =>
        {
            e.ToTable("ga_categories", Schema, t =>
            {
                t.HasCheckConstraint("ck_ga_categories_price", "price_minor >= 0");
                t.HasCheckConstraint("ck_ga_categories_capacity", "capacity >= 1");
                t.HasCheckConstraint("ck_ga_categories_counts", "held_count >= 0 AND sold_count >= 0 AND held_count + sold_count <= capacity");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Name).HasMaxLength(80);
            e.HasOne<Session>().WithMany().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.SessionId, x.Name }).IsUnique();
        });
    }
}
