using Loca.Api.Infrastructure.Idempotency;
using Loca.Api.Infrastructure.Metrics;
using Loca.Api.Infrastructure.Outbox;
using Loca.Api.Infrastructure.Persistence;
using Loca.Api.Infrastructure.Time;
using Loca.Api.Infrastructure.Tx;
using Loca.Api.Infrastructure.Workers;
using Loca.Api.Modules.Inventory;
using Loca.Api.Modules.Operations;
using Loca.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Loca.Api.Modules.Orders;

public enum ApplyOutcome { Applied, Stale, AlreadyFinal, Mismatch, UnknownReference }

public sealed record ReconcileResult(ReconciliationOutcome Outcome, string DetailCode, bool Attempted);

public static class OutboxTypes
{
    public const string PaymentInitiate = "payment.initiate";
    public const string RefundRequested = "refund.requested";
    public const string OrderPaid = "order.paid";
}

public sealed record PaymentInitiatePayload(Guid PaymentId);
public sealed record RefundRequestedPayload(Guid RefundId);
public sealed record OrderPaidPayload(Guid OrderId);

/// <summary>
/// Ödeme durum makinesinin tek uygulayıcısı. Senkron sağlayıcı yanıtı, webhook ve reconciliation aynı
/// <see cref="ApplyChargeStateInTransactionAsync"/> yolundan geçer; bu yüzden tekrar eden/sırasız olayların
/// iş etkisi tektir. Sağlayıcı çağrıları her zaman transaction dışındadır.
/// </summary>
public sealed class PaymentService(
    AppDbContext db, IClock clock, ITransactionRunner transactions, InventoryService inventory, IOutbox outbox, IAuditWriter audit,
    IPaymentProvider provider, IOptions<PspOptions> options, IFailpoints failpoints, AppMetrics metrics, ILogger<PaymentService> logger)
{
    // --- Başlatma (outbox: payment.initiate) -----------------------------------------------------

    public async Task InitiateAsync(Guid paymentId, CancellationToken ct)
    {
        var payment = await db.Payments.AsNoTracking().FirstOrDefaultAsync(p => p.Id == paymentId, ct);
        if (payment is null || payment.Status != PaymentStatus.Initiated) return; // Tekrarlı teslim: zaten ilerlemiş.

        await db.Payments.Where(p => p.Id == paymentId).ExecuteUpdateAsync(u => u.SetProperty(p => p.AttemptCount, p => p.AttemptCount + 1), ct);

        ProviderCharge charge;
        try
        {
            charge = await provider.CreateChargeAsync(payment.MerchantReference, payment.AmountMinor, payment.Currency, payment.MethodToken, ct);
        }
        catch (ProviderUnavailableException e)
        {
            // Zaman aşımı ret değildir: sonuç bilinmiyor. Sağlayıcı tahsilatı kaydetmiş olabilir.
            await MarkUnknownAsync(paymentId, e.ErrorCode, ct);
            return;
        }
        catch (ProviderRejectedException e)
        {
            await ApplyProviderErrorAsync(paymentId, e.ErrorCode, ct);
            return;
        }

        await ApplyChargeStateAsync(payment.MerchantReference, charge, "sync", ct);
    }

    private Task MarkUnknownAsync(Guid paymentId, string errorCode, CancellationToken ct) => transactions.RunAsync(async token =>
    {
        var payment = await LockAggregateAsync(paymentId, token);
        if (payment is null || payment.Value.Payment.Status is not (PaymentStatus.Initiated or PaymentStatus.Pending)) return 0;
        var (entity, _, _) = payment.Value;
        var now = await clock.NowAsync(token);
        metrics.Increment("loca_payments_total", ("outcome", "unknown"));
        entity.Status = PaymentStatus.Unknown;
        entity.FailureCode = errorCode;
        entity.NextReconcileAt = now.Add(ReconcileBackoff(entity.AttemptCount));
        entity.UpdatedAt = now;
        audit.Add(now, "payment.unknown", "payment", entity.Id, entity.Order.OrganizationId, new { errorCode }, actorKind: "Worker");
        await db.SaveChangesAsync(token);
        return 0;
    }, ct);

    private Task ApplyProviderErrorAsync(Guid paymentId, string errorCode, CancellationToken ct) => transactions.RunAsync(async token =>
    {
        var aggregate = await LockAggregateAsync(paymentId, token);
        if (aggregate is null || StateMachines.IsTerminal(aggregate.Value.Payment.Status)) return 0;
        var (payment, order, reservation) = aggregate.Value;
        var now = await clock.NowAsync(token);
        await CloseAsNotPaidAsync(payment, order, reservation, PaymentStatus.Failed, PaymentFailureKind.ProviderError, errorCode, now, token);
        await db.SaveChangesAsync(token);
        return 0;
    }, ct);

    // --- Durum uygulama --------------------------------------------------------------------------

    public Task<ApplyOutcome> ApplyChargeStateAsync(string merchantReference, ProviderCharge charge, string source, CancellationToken ct) =>
        transactions.RunAsync(token => ApplyChargeStateInTransactionAsync(merchantReference, charge, source, token), ct);

    /// <summary>Çağıranın transaction'ı içinde çalışır (webhook: inbox kaydıyla atomik).</summary>
    public async Task<ApplyOutcome> ApplyChargeStateInTransactionAsync(string merchantReference, ProviderCharge charge, string source, CancellationToken ct)
    {
        var paymentId = await db.Payments.AsNoTracking().Where(p => p.MerchantReference == merchantReference).Select(p => (Guid?)p.Id).FirstOrDefaultAsync(ct);
        if (paymentId is null) return ApplyOutcome.UnknownReference;

        var aggregate = await LockAggregateAsync(paymentId.Value, ct);
        if (aggregate is null) return ApplyOutcome.UnknownReference;
        var (payment, order, reservation) = aggregate.Value;
        var now = await clock.NowAsync(ct); // Kilit alındıktan sonra.
        await failpoints.HitAsync("payment.apply.after-lock", ct);

        if (charge.AmountMinor != payment.AmountMinor || !string.Equals(charge.Currency, payment.Currency, StringComparison.Ordinal))
        {
            // Tutar/para birimi uyuşmuyor: hiçbir durum değişmez, operatör incelemesine düşer.
            payment.ReviewReason = "amount_mismatch";
            payment.UpdatedAt = now;
            audit.Add(now, "payment.mismatch", "payment", payment.Id, order.OrganizationId, new { source }, actorKind: "Provider");
            await db.SaveChangesAsync(ct);
            return ApplyOutcome.Mismatch;
        }

        // Sırasız/tekrarlı olay: daha önce uygulanan sıradan büyük olmayan olay durum değiştirmez.
        if (charge.Sequence <= payment.LastProviderSequence) return ApplyOutcome.Stale;
        if (StateMachines.IsTerminal(payment.Status))
        {
            if (charge.Status == ProviderChargeStatus.Succeeded && payment.Status != PaymentStatus.Succeeded)
            {
                // Ret/hata olarak kapanmış bir ödeme için sağlayıcı sonradan başarı bildiriyor. Terminal durum geri
                // dönmez ve otomatik işlem yapılmaz; para hareketi olmuş olabileceği için operatör incelemesine düşer.
                payment.ReviewReason = "success_reported_after_final";
                payment.LastProviderSequence = charge.Sequence;
                payment.UpdatedAt = now;
                audit.Add(now, "payment.success_after_final", "payment", payment.Id, order.OrganizationId, new { source }, actorKind: "Provider");
                await db.SaveChangesAsync(ct);
            }
            return ApplyOutcome.AlreadyFinal;
        }

        payment.LastProviderSequence = charge.Sequence;
        payment.ProviderPaymentId ??= charge.ProviderPaymentId;
        payment.UpdatedAt = now;

        switch (charge.Status)
        {
            case ProviderChargeStatus.Pending:
                if (payment.Status != PaymentStatus.Pending)
                {
                    StateMachines.Ensure(StateMachines.CanTransition(payment.Status, PaymentStatus.Pending), payment.Status, PaymentStatus.Pending);
                    payment.Status = PaymentStatus.Pending;
                }
                payment.FailureCode = null;
                payment.NextReconcileAt = now.AddSeconds(options.Value.ReconcileIntervalSeconds);
                break;

            case ProviderChargeStatus.Succeeded:
                await ApplySuccessAsync(payment, order, reservation, source, now, ct);
                break;

            case ProviderChargeStatus.Declined:
                await CloseAsNotPaidAsync(payment, order, reservation, PaymentStatus.Declined, PaymentFailureKind.Declined, charge.DeclineCode ?? "declined", now, ct);
                break;

            case ProviderChargeStatus.Failed:
                await CloseAsNotPaidAsync(payment, order, reservation, PaymentStatus.Failed, PaymentFailureKind.ProviderError, charge.DeclineCode ?? "provider_failed", now, ct);
                break;
        }

        await db.SaveChangesAsync(ct);
        return ApplyOutcome.Applied;
    }

    private async Task ApplySuccessAsync(Payment payment, Order order, Reservation reservation, string source, DateTimeOffset now, CancellationToken ct)
    {
        StateMachines.Ensure(StateMachines.CanTransition(payment.Status, PaymentStatus.Succeeded), payment.Status, PaymentStatus.Succeeded);
        payment.Status = PaymentStatus.Succeeded;
        payment.FailureKind = null;
        payment.FailureCode = null;
        payment.ResolvedAt = now;
        payment.NextReconcileAt = null;

        // Kilit sırası: rezervasyon (alındı) → seans (FOR SHARE) → envanter.
        var sessionStatus = await db.Database
            .SqlQuery<string>($"SELECT status AS \"Value\" FROM catalog.sessions WHERE id = {order.SessionId} FOR SHARE").SingleAsync(ct);

        var payable = order.Status == OrderStatus.PendingPayment && sessionStatus != nameof(SessionStatus.Cancelled);
        if (payable && await inventory.TryConsumeAsync(reservation, now, ct))
        {
            // Geçerli hold atomik olarak tüketildi: Paid + bilet üretim niyeti aynı commit'te.
            metrics.Increment("loca_payments_total", ("outcome", "paid"));
            order.Status = OrderStatus.Paid;
            order.PaidAt = now;
            outbox.Enqueue(now, OutboxTypes.OrderPaid, new OrderPaidPayload(order.Id));
            audit.Add(now, "order.paid", "order", order.Id, order.OrganizationId, new { source }, actorKind: "Provider");
            return;
        }

        // Geç başarı: süre dolumu/iptal önce kazandı. Sipariş yeniden Paid olmaz, envanter yeniden kapılmaz,
        // bilet üretilmez; tek bir telafi iadesi kaydedilir (refunds.payment_id unique).
        metrics.Increment("loca_payments_total", ("outcome", "late_success_compensated"));
        payment.LateSuccessAt = now;
        var sessionCancelled = sessionStatus == nameof(SessionStatus.Cancelled);
        if (order.Status == OrderStatus.PendingPayment)
        {
            order.Status = sessionCancelled ? OrderStatus.Cancelled : OrderStatus.Expired;
            order.CancelReason = sessionCancelled ? OrderCancelReason.SessionCancelled : null;
            order.ClosedAt = now;
        }
        if (reservation.Status == ReservationStatus.Active)
            await inventory.CloseWithoutSaleAsync(reservation, sessionCancelled ? ReservationStatus.Released : ReservationStatus.Expired,
                sessionCancelled ? "SessionCancelled" : "Expired", now, ct);
        AddRefund(payment, order, RefundReason.LatePaymentCompensation, now);
        audit.Add(now, "payment.late_success_compensated", "payment", payment.Id, order.OrganizationId,
            new { source, orderStatus = order.Status.ToString() }, actorKind: "Provider");
    }

    private async Task CloseAsNotPaidAsync(Payment payment, Order order, Reservation reservation, PaymentStatus status,
        PaymentFailureKind kind, string code, DateTimeOffset now, CancellationToken ct)
    {
        StateMachines.Ensure(StateMachines.CanTransition(payment.Status, status), payment.Status, status);
        metrics.Increment("loca_payments_total", ("outcome", status == PaymentStatus.Declined ? "declined" : "provider_error"));
        payment.Status = status;
        payment.FailureKind = kind;
        payment.FailureCode = code;
        payment.ResolvedAt = now;
        payment.NextReconcileAt = null;
        payment.UpdatedAt = now;

        if (order.Status == OrderStatus.PendingPayment)
        {
            order.Status = OrderStatus.Failed;
            order.ClosedAt = now;
        }
        // Ödeme alınamadı: hold bekletilmez, envanter hemen serbest kalır.
        if (reservation.Status == ReservationStatus.Active)
            await inventory.CloseWithoutSaleAsync(reservation, ReservationStatus.Released, status == PaymentStatus.Declined ? "PaymentDeclined" : "PaymentFailed", now, ct);
        audit.Add(now, status == PaymentStatus.Declined ? "payment.declined" : "payment.failed", "payment", payment.Id, order.OrganizationId,
            new { code }, actorKind: "Provider");
    }

    /// <summary>İade kaydı + gönderim niyeti. Çağıran rezervasyon kilidini tutar.</summary>
    public Refund AddRefund(Payment payment, Order order, RefundReason reason, DateTimeOffset now)
    {
        var refund = new Refund
        {
            Id = Guid.CreateVersion7(), PaymentId = payment.Id, OrderId = order.Id, MerchantReference = $"ref_{payment.Id:N}", Reason = reason,
            Status = RefundStatus.Pending, AmountMinor = payment.AmountMinor, Currency = payment.Currency, CreatedAt = now, UpdatedAt = now,
        };
        db.Refunds.Add(refund);
        outbox.Enqueue(now, OutboxTypes.RefundRequested, new RefundRequestedPayload(refund.Id));
        return refund;
    }

    /// <summary>Kümenin kökünü kilitler ve ödeme/sipariş satırlarını kilit altında yükler.</summary>
    public async Task<(Payment Payment, Order Order, Reservation Reservation)?> LockAggregateAsync(Guid paymentId, CancellationToken ct)
    {
        var reservationId = await db.Payments.AsNoTracking().Where(p => p.Id == paymentId).Select(p => (Guid?)p.Order.ReservationId).FirstOrDefaultAsync(ct);
        if (reservationId is null) return null;
        var reservation = await inventory.LockReservationAsync(reservationId.Value, ct);
        if (reservation is null) return null;
        var payment = await db.Payments.Include(p => p.Order).Include(p => p.Refund).FirstAsync(p => p.Id == paymentId, ct);
        return (payment, payment.Order, reservation);
    }

    // --- Reconciliation --------------------------------------------------------------------------

    /// <summary>
    /// Belirsiz/bekleyen ödemeyi sağlayıcıdan sorgular. Sonuç, "kurtarma başarılı" ile "deneme yapıldı ama
    /// sonuç yok" ve "deneme başarısız" durumlarını ayrı raporlar.
    /// </summary>
    public async Task<ReconcileResult> ReconcileAsync(Guid paymentId, CancellationToken ct)
    {
        var snapshot = await db.Payments.AsNoTracking().Where(p => p.Id == paymentId)
            .Select(p => new { p.MerchantReference, p.Status, p.AmountMinor, p.Currency, p.MethodToken, OrderStatus = p.Order.Status, p.Order.ReservationId })
            .FirstOrDefaultAsync(ct);
        if (snapshot is null) return new(ReconciliationOutcome.Failed, "payment_not_found", Attempted: false);
        if (StateMachines.IsTerminal(snapshot.Status)) return new(ReconciliationOutcome.Planned, "already_final", Attempted: false);

        await db.Payments.Where(p => p.Id == paymentId).ExecuteUpdateAsync(u => u.SetProperty(p => p.AttemptCount, p => p.AttemptCount + 1), ct);
        try
        {
            var charge = await provider.GetChargeAsync(snapshot.MerchantReference, ct);
            if (charge is null)
            {
                // Sağlayıcıda kayıt yok: tahsilat hiç oluşmadı. Hold hâlâ geçerliyse aynı referansla gönderilir,
                // değilse ödeme "gönderilmedi" olarak kapatılır (para hareketi yok).
                var now = await clock.NowAsync(ct);
                var hold = await db.Reservations.AsNoTracking().Where(r => r.Id == snapshot.ReservationId).Select(r => new { r.Status, r.ExpiresAt }).FirstAsync(ct);
                var payable = snapshot.OrderStatus == OrderStatus.PendingPayment && hold.Status == ReservationStatus.Active && HoldPolicy.IsActive(now, hold.ExpiresAt);
                if (!payable)
                {
                    await ApplyProviderErrorAsync(paymentId, "not_submitted", ct);
                    return new(ReconciliationOutcome.Succeeded, "resolved_not_submitted", Attempted: true);
                }
                charge = await provider.CreateChargeAsync(snapshot.MerchantReference, snapshot.AmountMinor, snapshot.Currency, snapshot.MethodToken, ct);
            }

            var applied = await ApplyChargeStateAsync(snapshot.MerchantReference, charge, "reconcile", ct);
            if (applied == ApplyOutcome.Mismatch) return new(ReconciliationOutcome.Failed, "amount_mismatch", Attempted: true);

            var status = await db.Payments.AsNoTracking().Where(p => p.Id == paymentId).Select(p => p.Status).FirstAsync(ct);
            if (StateMachines.IsTerminal(status)) return new(ReconciliationOutcome.Succeeded, $"resolved_{status.ToString().ToLowerInvariant()}", Attempted: true);

            await ScheduleNextReconcileAsync(paymentId, ct);
            return new(ReconciliationOutcome.Attempted, "still_pending", Attempted: true);
        }
        catch (ProviderUnavailableException e)
        {
            await ScheduleNextReconcileAsync(paymentId, ct);
            return new(ReconciliationOutcome.Failed, $"provider_unavailable:{e.ErrorCode}", Attempted: true);
        }
        catch (ProviderRejectedException e)
        {
            await ScheduleNextReconcileAsync(paymentId, ct);
            return new(ReconciliationOutcome.Failed, $"provider_rejected:{e.ErrorCode}", Attempted: true);
        }
    }

    private async Task ScheduleNextReconcileAsync(Guid paymentId, CancellationToken ct)
    {
        var now = await clock.NowAsync(ct);
        var max = options.Value.ReconcileMaxAttempts;
        var attempts = await db.Payments.AsNoTracking().Where(p => p.Id == paymentId).Select(p => p.AttemptCount).FirstAsync(ct);
        // Bütçe bitince otomatik deneme durur; ödeme operatör kuyruğunda görünür kalır.
        DateTimeOffset? next = attempts >= max ? null : now.Add(ReconcileBackoff(attempts));
        await db.Payments.Where(p => p.Id == paymentId && (p.Status == PaymentStatus.Initiated || p.Status == PaymentStatus.Pending || p.Status == PaymentStatus.Unknown))
            .ExecuteUpdateAsync(u => u.SetProperty(p => p.NextReconcileAt, next).SetProperty(p => p.UpdatedAt, now), ct);
    }

    private TimeSpan ReconcileBackoff(int attempt)
    {
        var seconds = options.Value.ReconcileIntervalSeconds * Math.Pow(2, Math.Clamp(attempt - 1, 0, 6));
        var jitter = 1 + (Random.Shared.NextDouble() * 0.2 - 0.1);
        return TimeSpan.FromSeconds(Math.Min(seconds, 300) * jitter);
    }

    // --- İade gönderimi --------------------------------------------------------------------------

    /// <summary>
    /// İadeyi sağlayıcıya gönderir (kararlı iade referansıyla idempotent). Geçici hata
    /// <see cref="ProviderUnavailableException"/> olarak yükselir; çağıran (outbox) geri çekilerek yeniden dener.
    /// </summary>
    public async Task<RefundStatus?> DispatchRefundAsync(Guid refundId, CancellationToken ct)
    {
        var refund = await db.Refunds.AsNoTracking().Where(r => r.Id == refundId)
            .Select(r => new { r.Status, r.MerchantReference, r.AmountMinor, r.PaymentId, ChargeReference = r.Payment.MerchantReference })
            .FirstOrDefaultAsync(ct);
        if (refund is null) return null;
        if (refund.Status != RefundStatus.Pending) return refund.Status;

        await db.Refunds.Where(r => r.Id == refundId).ExecuteUpdateAsync(u => u.SetProperty(r => r.AttemptCount, r => r.AttemptCount + 1), ct);

        ProviderRefund result;
        try
        {
            result = await provider.CreateRefundAsync(refund.MerchantReference, refund.ChargeReference, refund.AmountMinor, ct);
        }
        catch (ProviderRejectedException e)
        {
            return await ResolveRefundAsync(refund.PaymentId, RefundStatus.Failed, null, $"provider_rejected:{e.ErrorCode}", ct);
        }

        return result.Status == ProviderRefundStatus.Succeeded
            ? await ResolveRefundAsync(refund.PaymentId, RefundStatus.Succeeded, result.ProviderRefundId, null, ct)
            : await ResolveRefundAsync(refund.PaymentId, RefundStatus.Failed, result.ProviderRefundId, result.FailureCode ?? "refund_failed", ct);
    }

    public Task<RefundStatus?> ResolveRefundAsync(Guid paymentId, RefundStatus status, string? providerRefundId, string? errorCode, CancellationToken ct) =>
        transactions.RunAsync<RefundStatus?>(async token =>
        {
            var aggregate = await LockAggregateAsync(paymentId, token);
            var refund = aggregate?.Payment.Refund;
            if (aggregate is null || refund is null) return null;
            if (refund.Status != RefundStatus.Pending) return refund.Status; // Tekrarlı teslim: etkisiz.

            var now = await clock.NowAsync(token);
            StateMachines.Ensure(StateMachines.CanTransition(refund.Status, status), refund.Status, status);
            metrics.Increment("loca_refunds_total", ("outcome", status == RefundStatus.Succeeded ? "succeeded" : "failed"));
            refund.Status = status;
            refund.ProviderRefundId ??= providerRefundId;
            refund.LastErrorCode = errorCode;
            refund.UpdatedAt = now;
            refund.ResolvedAt = status == RefundStatus.Succeeded ? now : null;
            audit.Add(now, status == RefundStatus.Succeeded ? "refund.succeeded" : "refund.failed", "refund", refund.Id,
                aggregate.Value.Order.OrganizationId, new { errorCode, reason = refund.Reason.ToString() }, actorKind: "Worker");
            await db.SaveChangesAsync(token);
            return refund.Status;
        }, ct);

    internal void LogLostLease(Guid paymentId) =>
        logger.LogWarning("Reconciliation lease for payment {PaymentId} was lost before release.", paymentId);
}

internal sealed class PaymentInitiateHandler(PaymentService payments) : IOutboxHandler
{
    public string Type => OutboxTypes.PaymentInitiate;
    public Task HandleAsync(OutboxMessage message, CancellationToken ct) => payments.InitiateAsync(message.Read<PaymentInitiatePayload>().PaymentId, ct);
}

internal sealed class RefundRequestedHandler(PaymentService payments, AppDbContext db) : IOutboxHandler
{
    public string Type => OutboxTypes.RefundRequested;

    public Task HandleAsync(OutboxMessage message, CancellationToken ct) => payments.DispatchRefundAsync(message.Read<RefundRequestedPayload>().RefundId, ct);

    public async Task OnDeadLetteredAsync(OutboxMessage message, CancellationToken ct)
    {
        // Deneme bütçesi bitti: iade "Failed" olur ve operatör kuyruğunda görünür. Para iadesi YAPILMAMIŞTIR.
        var refundId = message.Read<RefundRequestedPayload>().RefundId;
        var paymentId = await db.Refunds.AsNoTracking().Where(r => r.Id == refundId).Select(r => (Guid?)r.PaymentId).FirstOrDefaultAsync(ct);
        if (paymentId is not null)
            await payments.ResolveRefundAsync(paymentId.Value, RefundStatus.Failed, null, "dispatch_exhausted", ct);
    }
}

/// <summary>Rezervasyon satış olmadan kapanırsa bekleyen siparişi de kapatır (aynı transaction, aynı kilit).</summary>
internal sealed class OrderReservationHook(AppDbContext db) : IReservationLifecycleHook
{
    public async Task OnClosedWithoutSaleAsync(Reservation reservation, ReservationStatus closedAs, DateTimeOffset now, CancellationToken ct)
    {
        var order = await db.Orders.FirstOrDefaultAsync(o => o.ReservationId == reservation.Id, ct);
        if (order is null || order.Status != OrderStatus.PendingPayment) return;
        // Hold süresi doldu; ödeme hâlâ sonuçlanmamış olabilir. Sipariş kapanır, hold uzamaz. Ödeme sonradan
        // başarıyla dönerse "geç başarı" yolu telafi iadesi üretir.
        order.Status = OrderStatus.Expired;
        order.ClosedAt = now;
    }
}

internal sealed class HoldOrderLookup(AppDbContext db) : IHoldOrderLookup
{
    public Task<Guid?> FindOrderIdAsync(Guid reservationId, CancellationToken ct) =>
        db.Orders.AsNoTracking().Where(o => o.ReservationId == reservationId).Select(o => (Guid?)o.Id).FirstOrDefaultAsync(ct);
}

/// <summary>
/// Bekleyen/belirsiz ödemeleri zamanı geldiğinde sağlayıcıdan sorgular. Sahiplik lease + fencing token ile;
/// yapılan iş (durum uygulama) zaten kök kilit ve sıra numarasıyla korunduğundan, lease'i kaybeden eski sahip
/// yeni sahibin sonucunu ezemez.
/// </summary>
public sealed class PaymentReconciliationProcessor(
    AppDbContext db, IClock clock, PaymentService payments, IOptions<PspOptions> options) : IBatchProcessor
{
    private readonly string _owner = $"{Environment.MachineName}:{Guid.NewGuid():N}";

    public string Name => "payment-reconciliation";

    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var claimed = await ClaimAsync(10, ct);
        foreach (var payment in claimed)
        {
            await payments.ReconcileAsync(payment.Id, ct);
            var released = await db.Payments.Where(p => p.Id == payment.Id && p.LeaseToken == payment.LeaseToken)
                .ExecuteUpdateAsync(u => u.SetProperty(p => p.LeaseOwner, (string?)null).SetProperty(p => p.LeaseUntil, (DateTimeOffset?)null), ct);
            if (released == 0) payments.LogLostLease(payment.Id);
        }
        return claimed.Count;
    }

    public async Task<List<Payment>> ClaimAsync(int batchSize, CancellationToken ct)
    {
        var now = await clock.NowAsync(ct);
        var leaseUntil = now.AddSeconds(options.Value.ReconcileLeaseSeconds);
        return await db.Payments.FromSql($"""
            UPDATE orders.payments AS p
            SET lease_owner = {_owner}, lease_token = gen_random_uuid(), lease_until = {leaseUntil}
            WHERE p.id IN (
                SELECT id FROM orders.payments
                WHERE status IN ('Initiated','Pending','Unknown') AND next_reconcile_at IS NOT NULL AND next_reconcile_at <= {now}
                  AND (lease_until IS NULL OR lease_until <= {now})
                ORDER BY next_reconcile_at, id LIMIT {batchSize} FOR UPDATE SKIP LOCKED)
            RETURNING p.*
            """).AsNoTracking().ToListAsync(ct);
    }
}
