using Loca.Api.Modules.Identity;
using Loca.Domain;
using Microsoft.EntityFrameworkCore;

namespace Loca.Api.Modules.Catalog;

public sealed class Venue
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public string Name { get; set; } = "";
    public string City { get; set; } = "";
    public string Address { get; set; } = "";

    /// <summary>IANA timezone kimliği (ör. Europe/Istanbul).</summary>
    public string Timezone { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Değişmez koltuk planı sürümü. Düzenleme yoktur; değişiklik yeni sürüm üretir.</summary>
public sealed class SeatLayout
{
    public Guid Id { get; set; }
    public Guid VenueId { get; set; }
    public Venue Venue { get; set; } = null!;
    public int Version { get; set; }
    public string Name { get; set; } = "";
    public int SeatCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class LayoutSeat
{
    public Guid Id { get; set; }
    public Guid LayoutId { get; set; }
    public string Section { get; set; } = "";
    public string RowLabel { get; set; } = "";
    public int SeatNumber { get; set; }

    /// <summary>Plan koordinatı, onda bir koltuk birimi cinsinden.</summary>
    public int X { get; set; }
    public int Y { get; set; }

    public string TierCode { get; set; } = "";
}

public sealed class Event
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Organization Organization { get; set; } = null!;
    public string Title { get; set; } = "";
    public string Slug { get; set; } = "";
    public string Description { get; set; } = "";
    public EventCategory Category { get; set; }
    public EventStatus Status { get; set; }

    /// <summary>Platform yöneticisi yayından kaldırdıysa dolu; public görünürlüğü durumdan bağımsız keser.</summary>
    public DateTimeOffset? ModerationBlockedAt { get; set; }

    public bool IsDemo { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public List<Session> Sessions { get; set; } = [];
}

public sealed class Session
{
    public Guid Id { get; set; }
    public Guid EventId { get; set; }
    public Event Event { get; set; } = null!;

    /// <summary>Sahiplik kontrolü için etkinlikten kopyalanır; değişmez.</summary>
    public Guid OrganizationId { get; set; }

    public Guid VenueId { get; set; }
    public Venue Venue { get; set; } = null!;
    public string City { get; set; } = "";
    public string Timezone { get; set; } = "";
    public DateTimeOffset StartsAt { get; set; }
    public DateTimeOffset SalesStartAt { get; set; }
    public DateTimeOffset SalesEndAt { get; set; }
    public SalesMode SalesMode { get; set; }
    public SessionStatus Status { get; set; }
    public int HoldTtlSeconds { get; set; }
    public Guid? LayoutId { get; set; }

    /// <summary>Seans bazında, opt-in demo simülatörü (docs/01 §6).</summary>
    public bool DemoSimulatorEnabled { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
}

internal static class CatalogModel
{
    public const string Schema = "catalog";

    public static void Configure(ModelBuilder b)
    {
        b.Entity<Venue>(e =>
        {
            e.ToTable("venues", Schema);
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Name).HasMaxLength(160);
            e.Property(x => x.City).HasMaxLength(80);
            e.Property(x => x.Address).HasMaxLength(300);
            e.Property(x => x.Timezone).HasMaxLength(64);
            e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.OrganizationId);
        });

        b.Entity<SeatLayout>(e =>
        {
            e.ToTable("seat_layouts", Schema);
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Name).HasMaxLength(120);
            e.HasOne(x => x.Venue).WithMany().HasForeignKey(x => x.VenueId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.VenueId, x.Version }).IsUnique();
        });

        b.Entity<LayoutSeat>(e =>
        {
            e.ToTable("layout_seats", Schema);
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Section).HasMaxLength(40);
            e.Property(x => x.RowLabel).HasMaxLength(4);
            e.Property(x => x.TierCode).HasMaxLength(8);
            e.HasOne<SeatLayout>().WithMany().HasForeignKey(x => x.LayoutId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.LayoutId, x.Section, x.RowLabel, x.SeatNumber }).IsUnique();
        });

        b.Entity<Event>(e =>
        {
            e.ToTable("events", Schema, t =>
            {
                t.HasCheckConstraint("ck_events_status", "status IN ('Draft','Published','Cancelled')");
                t.HasCheckConstraint("ck_events_category", "category IN ('Concert','Theatre','Festival','Sports','Standup','Conference','Other')");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Title).HasMaxLength(160);
            e.Property(x => x.Slug).HasMaxLength(80);
            e.Property(x => x.Description).HasMaxLength(4000);
            e.Property(x => x.Category).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.HasOne(x => x.Organization).WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.Slug).IsUnique();
            e.HasIndex(x => new { x.OrganizationId, x.CreatedAt });
        });

        b.Entity<Session>(e =>
        {
            e.ToTable("sessions", Schema, t =>
            {
                t.HasCheckConstraint("ck_sessions_status", "status IN ('Draft','Published','Cancelled')");
                t.HasCheckConstraint("ck_sessions_sales_mode", "sales_mode IN ('ReservedSeating','GeneralAdmission')");
                t.HasCheckConstraint("ck_sessions_sales_window", "sales_start_at < sales_end_at AND sales_end_at <= starts_at");
                t.HasCheckConstraint("ck_sessions_hold_ttl", "hold_ttl_seconds BETWEEN 5 AND 1800");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.City).HasMaxLength(80);
            e.Property(x => x.Timezone).HasMaxLength(64);
            e.Property(x => x.SalesMode).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.HasOne(x => x.Event).WithMany(ev => ev.Sessions).HasForeignKey(x => x.EventId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Venue).WithMany().HasForeignKey(x => x.VenueId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<SeatLayout>().WithMany().HasForeignKey(x => x.LayoutId).OnDelete(DeleteBehavior.Restrict);
            // Public keşif: yayınlanmış seanslar başlangıç anına göre keyset ile gezilir.
            e.HasIndex(x => new { x.StartsAt, x.Id }).HasFilter("status = 'Published'").HasDatabaseName("ix_sessions_public_discovery");
            e.HasIndex(x => x.EventId);
            e.HasIndex(x => x.OrganizationId);
        });
    }
}
