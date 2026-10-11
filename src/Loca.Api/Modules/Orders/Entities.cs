using Loca.Api.Modules.Catalog;
using Loca.Api.Modules.Identity;
using Loca.Api.Modules.Inventory;
using Loca.Domain;
using Microsoft.EntityFrameworkCore;

namespace Loca.Api.Modules.Orders;

public sealed class Order
{
    public Guid Id { get; set; }

    /// <summary>İş düzeyi tekillik: bir hold için en fazla bir sipariş (unique). Kilit kökü bu rezervasyondur.</summary>
    public Guid ReservationId { get; set; }

    public Guid UserId { get; set; }
    public Guid SessionId { get; set; }
    public Guid OrganizationId { get; set; }
    public string OrderNumber { get; set; } = "";
    public OrderStatus Status { get; set; }
    public long TotalMinor { get; set; }
    public string Currency { get; set; } = Money.SupportedCurrency;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? PaidAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public OrderCancelReason? CancelReason { get; set; }
    public Payment? Payment { get; set; }
}

public sealed class Payment
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public Order Order { get; set; } = null!;

    /// <summary>Sağlayıcıya gönderilen kararlı referans; aynı referansla tekrar yeni tahsilat yaratmaz.</summary>
    public string MerchantReference { get; set; } = "";

    public string? ProviderPaymentId { get; set; }
    public PaymentStatus Status { get; set; }
    public long AmountMinor { get; set; }
    public string Currency { get; set; } = Money.SupportedCurrency;
    public string MethodToken { get; set; } = "";
    public PaymentFailureKind? FailureKind { get; set; }
    public string? FailureCode { get; set; }

    /// <summary>Uygulanan son sağlayıcı olayının sırası; daha küçük/eşit sıralı olay durum değiştirmez.</summary>
    public int LastProviderSequence { get; set; }

    public int AttemptCount { get; set; }
    public DateTimeOffset? NextReconcileAt { get; set; }
    public string? LeaseOwner { get; set; }
    public Guid? LeaseToken { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }

    /// <summary>Başarı, sipariş artık ödenebilir değilken geldiyse dolu: telafi iadesi üretilmiştir.</summary>
    public DateTimeOffset? LateSuccessAt { get; set; }

    /// <summary>Operatör incelemesi gerektiren durum kodu (ör. amount_mismatch).</summary>
    public string? ReviewReason { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public Refund? Refund { get; set; }
}

public sealed class Refund
{
    public Guid Id { get; set; }

    /// <summary>İş düzeyi tekillik: ödeme başına tek (tam) iade.</summary>
    public Guid PaymentId { get; set; }

    public Payment Payment { get; set; } = null!;
    public Guid OrderId { get; set; }
    public string MerchantReference { get; set; } = "";
    public string? ProviderRefundId { get; set; }
    public RefundReason Reason { get; set; }
    public RefundStatus Status { get; set; }
    public long AmountMinor { get; set; }
    public string Currency { get; set; } = Money.SupportedCurrency;
    public int AttemptCount { get; set; }
    public string? LastErrorCode { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
}

/// <summary>Operatörün başlattığı kurtarma denemesi. Planlandı / denendi / başarılı / başarısız ayrı tutulur.</summary>
public sealed class ReconciliationRun
{
    public Guid Id { get; set; }
    public string TargetType { get; set; } = "";
    public Guid TargetId { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid? RequestedBy { get; set; }
    public ReconciliationOutcome Outcome { get; set; }
    public string DetailCode { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

internal static class OrdersModel
{
    public const string Schema = "orders";
    public const string OrderPerReservationIndex = "ux_orders_reservation";
    public const string RefundPerPaymentIndex = "ux_refunds_payment";

    public static void Configure(ModelBuilder b)
    {
        b.Entity<Order>(e =>
        {
            e.ToTable("orders", Schema, t =>
            {
                t.HasCheckConstraint("ck_orders_status", "status IN ('PendingPayment','Paid','Failed','Expired','Cancelled')");
                t.HasCheckConstraint("ck_orders_currency", "currency = 'TRY'");
                t.HasCheckConstraint("ck_orders_total", "total_minor >= 0");
                t.HasCheckConstraint("ck_orders_cancel_reason", "cancel_reason IS NULL OR cancel_reason IN ('Customer','SessionCancelled')");
                t.HasCheckConstraint("ck_orders_paid_at", "status <> 'Paid' OR paid_at IS NOT NULL");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.OrderNumber).HasMaxLength(16);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.CancelReason).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Currency).HasMaxLength(3).IsFixedLength();
            e.HasOne<Reservation>().WithMany().HasForeignKey(x => x.ReservationId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Session>().WithMany().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.ReservationId).IsUnique().HasDatabaseName(OrderPerReservationIndex);
            e.HasIndex(x => x.OrderNumber).IsUnique();
            e.HasIndex(x => new { x.UserId, x.CreatedAt });
            e.HasIndex(x => new { x.SessionId, x.Status });
            e.HasIndex(x => x.OrganizationId);
        });

        b.Entity<Payment>(e =>
        {
            e.ToTable("payments", Schema, t =>
            {
                t.HasCheckConstraint("ck_payments_status", "status IN ('Initiated','Pending','Unknown','Succeeded','Declined','Failed')");
                t.HasCheckConstraint("ck_payments_failure_kind", "failure_kind IS NULL OR failure_kind IN ('Declined','ProviderError')");
                // Ret ile sağlayıcı hatası karışmaz: durum ile neden türü birbirini doğrular.
                t.HasCheckConstraint("ck_payments_failure_consistency",
                    "(status = 'Declined' AND failure_kind = 'Declined') OR (status = 'Failed' AND failure_kind = 'ProviderError') OR " +
                    "(status NOT IN ('Declined','Failed') AND failure_kind IS NULL)");
                t.HasCheckConstraint("ck_payments_currency", "currency = 'TRY'");
                t.HasCheckConstraint("ck_payments_amount", "amount_minor >= 0");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.MerchantReference).HasMaxLength(64);
            e.Property(x => x.ProviderPaymentId).HasMaxLength(64);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(12);
            e.Property(x => x.FailureKind).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.FailureCode).HasMaxLength(60);
            e.Property(x => x.MethodToken).HasMaxLength(40);
            e.Property(x => x.Currency).HasMaxLength(3).IsFixedLength();
            e.Property(x => x.LeaseOwner).HasMaxLength(80);
            e.Property(x => x.ReviewReason).HasMaxLength(40);
            e.HasOne(x => x.Order).WithOne(o => o.Payment).HasForeignKey<Payment>(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.OrderId).IsUnique();
            e.HasIndex(x => x.MerchantReference).IsUnique();
            e.HasIndex(x => x.NextReconcileAt).HasFilter("status IN ('Initiated','Pending','Unknown') AND next_reconcile_at IS NOT NULL").HasDatabaseName("ix_payments_reconcile_due");
        });

        b.Entity<Refund>(e =>
        {
            e.ToTable("refunds", Schema, t =>
            {
                t.HasCheckConstraint("ck_refunds_status", "status IN ('Pending','Succeeded','Failed')");
                t.HasCheckConstraint("ck_refunds_reason", "reason IN ('CustomerCancellation','SessionCancellation','LatePaymentCompensation')");
                t.HasCheckConstraint("ck_refunds_currency", "currency = 'TRY'");
                t.HasCheckConstraint("ck_refunds_amount", "amount_minor > 0");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.MerchantReference).HasMaxLength(64);
            e.Property(x => x.ProviderRefundId).HasMaxLength(64);
            e.Property(x => x.Reason).HasConversion<string>().HasMaxLength(28);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(12);
            e.Property(x => x.Currency).HasMaxLength(3).IsFixedLength();
            e.Property(x => x.LastErrorCode).HasMaxLength(60);
            e.HasOne(x => x.Payment).WithOne(p => p.Refund).HasForeignKey<Refund>(x => x.PaymentId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Order>().WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.PaymentId).IsUnique().HasDatabaseName(RefundPerPaymentIndex);
            e.HasIndex(x => x.MerchantReference).IsUnique();
            e.HasIndex(x => x.Status);
            e.HasIndex(x => x.OrderId);
        });

        b.Entity<ReconciliationRun>(e =>
        {
            e.ToTable("reconciliation_runs", Schema, t =>
            {
                t.HasCheckConstraint("ck_reconciliation_runs_outcome", "outcome IN ('Planned','Attempted','Succeeded','Failed')");
                t.HasCheckConstraint("ck_reconciliation_runs_target", "target_type IN ('payment','refund')");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.TargetType).HasMaxLength(12);
            e.Property(x => x.Outcome).HasConversion<string>().HasMaxLength(12);
            e.Property(x => x.DetailCode).HasMaxLength(60);
            e.HasIndex(x => new { x.TargetType, x.TargetId });
            e.HasIndex(x => new { x.OrganizationId, x.CreatedAt });
        });
    }
}
