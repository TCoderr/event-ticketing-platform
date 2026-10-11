using Loca.Api.Infrastructure.Outbox;
using Loca.Api.Infrastructure.Persistence;
using Loca.Api.Infrastructure.Time;
using Loca.Api.Infrastructure.Tx;
using Loca.Api.Modules.Catalog;
using Loca.Api.Modules.Identity;
using Loca.Api.Modules.Inventory;
using Loca.Api.Modules.Operations;
using Loca.Api.Modules.Orders;
using Loca.Domain;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Loca.Api.Modules.Tickets;

public sealed class Ticket
{
    /// <summary>Deterministik: UUIDv5(sipariş, satır, sıra). Tekrarlanan üretim aynı kimliğe çarpar.</summary>
    public Guid Id { get; set; }

    public Guid OrderId { get; set; }
    public Guid ReservationId { get; set; }
    public Guid ReservationLineId { get; set; }
    public int Ordinal { get; set; }
    public Guid SessionId { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid OwnerUserId { get; set; }
    public Guid? SessionSeatId { get; set; }
    public Guid? GaCategoryId { get; set; }
    public string Label { get; set; } = "";
    public TicketStatus Status { get; set; }

    /// <summary>SHA-256(token). Token'ın kendisi düz metin olarak saklanmaz.</summary>
    public byte[] TokenHash { get; set; } = [];

    /// <summary>Sahibine yeniden gösterebilmek için Data Protection ile şifrelenmiş token.</summary>
    public string TokenProtected { get; set; } = "";

    public DateTimeOffset IssuedAt { get; set; }
    public DateTimeOffset? CheckedInAt { get; set; }
    public Guid? CheckedInBy { get; set; }
    public string? CheckedInGate { get; set; }
    public DateTimeOffset? VoidedAt { get; set; }
    public string? VoidReason { get; set; }
}

public sealed class GateAssignment
{
    public Guid SessionId { get; set; }
    public Guid UserId { get; set; }
    public Guid AssignedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Yalnız ekleme yapılan tarama kaydı. Token içermez.</summary>
public sealed class ScanLogEntry
{
    public long Id { get; set; }
    public Guid SessionId { get; set; }
    public Guid? TicketId { get; set; }
    public Guid StaffUserId { get; set; }
    public string Result { get; set; } = "";
    public string? Gate { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

internal static class TicketsModel
{
    public const string Schema = "tickets";

    public static void Configure(ModelBuilder b)
    {
        b.Entity<Ticket>(e =>
        {
            e.ToTable("tickets", Schema, t =>
            {
                t.HasCheckConstraint("ck_tickets_status", "status IN ('Valid','CheckedIn','Cancelled')");
                t.HasCheckConstraint("ck_tickets_ordinal", "ordinal BETWEEN 1 AND 6");
                t.HasCheckConstraint("ck_tickets_checked_in", "status <> 'CheckedIn' OR checked_in_at IS NOT NULL");
                t.HasCheckConstraint("ck_tickets_valid_unused", "status <> 'Valid' OR checked_in_at IS NULL");
                t.HasCheckConstraint("ck_tickets_voided", "(status = 'Cancelled') = (voided_at IS NOT NULL)");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Label).HasMaxLength(140);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(12);
            e.Property(x => x.TokenProtected).HasMaxLength(600);
            e.Property(x => x.CheckedInGate).HasMaxLength(40);
            e.Property(x => x.VoidReason).HasMaxLength(32);
            e.HasOne<Order>().WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Reservation>().WithMany().HasForeignKey(x => x.ReservationId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ReservationLine>().WithMany().HasForeignKey(x => x.ReservationLineId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Session>().WithMany().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.OwnerUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.ReservationLineId, x.Ordinal }).IsUnique();
            e.HasIndex(x => x.TokenHash).IsUnique();
            // Ek savunma: bir koltuk için aynı anda en fazla bir geçerli bilet.
            e.HasIndex(x => x.SessionSeatId).IsUnique().HasFilter("session_seat_id IS NOT NULL AND status IN ('Valid','CheckedIn')").HasDatabaseName("ux_tickets_one_live_ticket_per_seat");
            e.HasIndex(x => new { x.OwnerUserId, x.IssuedAt });
            e.HasIndex(x => x.OrderId);
            e.HasIndex(x => new { x.SessionId, x.Status });
        });

        b.Entity<GateAssignment>(e =>
        {
            e.ToTable("gate_assignments", Schema);
            e.HasKey(x => new { x.SessionId, x.UserId });
            e.HasOne<Session>().WithMany().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.UserId);
        });

        b.Entity<ScanLogEntry>(e =>
        {
            e.ToTable("scan_log", Schema);
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).UseIdentityAlwaysColumn();
            e.Property(x => x.Result).HasMaxLength(32);
            e.Property(x => x.Gate).HasMaxLength(40);
            e.HasIndex(x => new { x.SessionId, x.CreatedAt });
        });
    }
}

/// <summary>Bilet token'ını saklamak için şifreler/çözer. Anahtarlar veritabanında değildir (ADR 0009).</summary>
public sealed class TicketTokenProtector(IDataProtectionProvider provider)
{
    private readonly IDataProtector _protector = provider.CreateProtector("Loca.TicketToken.v1");
    public string Protect(string token) => _protector.Protect(token);
    public string Unprotect(string protectedToken) => _protector.Unprotect(protectedToken);
}

public sealed class TicketService(
    AppDbContext db, IClock clock, ITransactionRunner transactions, InventoryService inventory, TicketTokenProtector protector, IAuditWriter audit)
{
    /// <summary>
    /// Ödenmiş siparişin biletlerini üretir. Tekrar çağrılabilir: bilet kimliği deterministiktir ve
    /// <c>ON CONFLICT (id) DO NOTHING</c> ile ikinci üretim etkisizdir. Sipariş bu arada iptal edildiyse hiçbir şey üretmez.
    /// </summary>
    public Task<int> IssueForOrderAsync(Guid orderId, CancellationToken ct) => transactions.RunAsync(async token =>
    {
        var reservationId = await db.Orders.AsNoTracking().Where(o => o.Id == orderId).Select(o => (Guid?)o.ReservationId).FirstOrDefaultAsync(token);
        if (reservationId is null) return 0;
        await inventory.LockReservationAsync(reservationId.Value, token); // Küme kökü: iptal ile yarışmaz, sırayla çalışır.
        var order = await db.Orders.AsNoTracking().FirstAsync(o => o.Id == orderId, token);
        if (order.Status != OrderStatus.Paid) return 0;

        var now = await clock.NowAsync(token);
        var lines = await db.ReservationLines.AsNoTracking().Where(l => l.ReservationId == order.ReservationId).OrderBy(l => l.LineNo).ToListAsync(token);
        var issued = 0;
        foreach (var line in lines)
        {
            for (var ordinal = 1; ordinal <= line.Quantity; ordinal++)
            {
                var id = TicketIdentity.For(order.Id, line.Id, ordinal);
                var bearer = TicketToken.Generate();
                var hash = TicketToken.Hash(bearer);
                var sealedToken = protector.Protect(bearer);
                var label = line.Quantity > 1 ? $"{line.Label} · {ordinal}/{line.Quantity}" : line.Label;
                issued += await db.Database.ExecuteSqlAsync($"""
                    INSERT INTO tickets.tickets (id, order_id, reservation_id, reservation_line_id, ordinal, session_id, organization_id, owner_user_id,
                        session_seat_id, ga_category_id, label, status, token_hash, token_protected, issued_at)
                    VALUES ({id}, {order.Id}, {order.ReservationId}, {line.Id}, {ordinal}, {order.SessionId}, {order.OrganizationId}, {order.UserId},
                        {line.SessionSeatId}, {line.GaCategoryId}, {label}, 'Valid', {hash}, {sealedToken}, {now})
                    ON CONFLICT (id) DO NOTHING
                    """, token);
            }
        }
        if (issued > 0)
        {
            audit.Add(now, "tickets.issued", "order", order.Id, order.OrganizationId, new { count = issued }, actorKind: "Worker");
            await db.SaveChangesAsync(token);
        }
        return issued;
    }, ct);

    /// <summary>
    /// Siparişin biletlerini geçersiz kılar. Çağıran rezervasyon kilidini tutar. İadenin sonucu beklenmez:
    /// bilet, iptal commit'i ile aynı anda geçersizdir.
    /// </summary>
    public async Task<int> VoidForOrderAsync(Guid orderId, string reason, bool includeCheckedIn, DateTimeOffset now, CancellationToken ct) =>
        await db.Tickets
            .Where(t => t.OrderId == orderId && (t.Status == TicketStatus.Valid || (includeCheckedIn && t.Status == TicketStatus.CheckedIn)))
            .ExecuteUpdateAsync(u => u
                .SetProperty(t => t.Status, TicketStatus.Cancelled)
                .SetProperty(t => t.VoidedAt, now)
                .SetProperty(t => t.VoidReason, reason), ct);

    public Task<bool> AnyCheckedInAsync(Guid orderId, CancellationToken ct) =>
        db.Tickets.AnyAsync(t => t.OrderId == orderId && t.Status == TicketStatus.CheckedIn, ct);
}

internal sealed class OrderPaidHandler(TicketService tickets) : IOutboxHandler
{
    public string Type => OutboxTypes.OrderPaid;
    public Task HandleAsync(OutboxMessage message, CancellationToken ct) => tickets.IssueForOrderAsync(message.Read<OrderPaidPayload>().OrderId, ct);
}
