using System.Security.Cryptography;
using Loca.Api.Infrastructure.Auth;
using Loca.Api.Infrastructure.Config;
using Loca.Api.Infrastructure.Http;
using Loca.Api.Infrastructure.Idempotency;
using Loca.Api.Infrastructure.Metrics;
using Loca.Api.Infrastructure.Outbox;
using Loca.Api.Infrastructure.Persistence;
using Loca.Api.Infrastructure.RateLimiting;
using Loca.Api.Infrastructure.Time;
using Loca.Api.Infrastructure.Tx;
using Loca.Api.Infrastructure.Workers;
using Loca.Api.Modules.Catalog;
using Loca.Api.Modules.Inventory;
using Loca.Api.Modules.Operations;
using Loca.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Loca.Api.Modules.Orders;

public sealed record CheckoutRequest(string PaymentMethod);
public sealed record OrderPaymentDto(PaymentStatus Status, PaymentFailureKind? FailureKind, string? FailureCode, bool LateSuccess);
public sealed record OrderRefundDto(RefundStatus Status, RefundReason Reason, long AmountMinor);
public sealed record OrderDto(Guid Id, string OrderNumber, OrderStatus Status, long TotalMinor, string Currency, DateTimeOffset CreatedAt,
    DateTimeOffset? PaidAt, DateTimeOffset? ClosedAt, OrderCancelReason? CancelReason, Guid HoldId, DateTimeOffset HoldExpiresAt,
    Guid SessionId, string EventTitle, string VenueName, string City, DateTimeOffset StartsAt, string StartsAtLocal, string Timezone,
    SessionStatus SessionStatus, int TicketCount, IReadOnlyList<HoldLineDto> Lines, OrderPaymentDto Payment, OrderRefundDto? Refund,
    bool CanCancel, DateTimeOffset CancelDeadline, DateTimeOffset ServerTime, string Note, bool IsDemo);
public sealed record OperatorRunDto(Guid RunId, string TargetType, Guid TargetId, bool Planned, bool Attempted, ReconciliationOutcome Outcome, string DetailCode);

public sealed class OrderService(
    AppDbContext db, ICurrentUser currentUser, IClock clock, InventoryService inventory, IOutbox outbox, IAuditWriter audit,
    IOptions<PspOptions> psp, IOptions<DemoOptions> demo, IOptions<PolicyOptions> policies)
{
    /// <summary>202 yanıtının anlamı: sipariş kaydedildi, ödeme HENÜZ alınmadı.</summary>
    public const string AcceptedNote = "order_recorded_payment_not_yet_confirmed";

    /// <summary>İdempotency transaction'ı içinde çalışır; sağlayıcı çağrısı yapmaz (o, outbox handler'ındadır).</summary>
    public async Task<IdempotentResult> CheckoutAsync(Guid holdId, CheckoutRequest request, CancellationToken ct, Guid? actingUserId = null)
    {
        var userId = actingUserId ?? currentUser.RequireUserId();
        var reservation = await inventory.LockReservationAsync(holdId, ct);
        if (reservation is null || reservation.UserId != userId) throw ApiErrors.NotFound();
        var now = await clock.NowAsync(ct);

        // Farklı Idempotency-Key ile ikinci checkout: kilit altında mevcut sipariş görülür.
        var existing = await db.Orders.AsNoTracking().Where(o => o.ReservationId == holdId).Select(o => (Guid?)o.Id).FirstOrDefaultAsync(ct);
        if (existing is not null)
            throw ApiErrors.Conflict(ErrorCodes.OrderAlreadyExists, "A checkout was already started for this hold.",
                new Dictionary<string, object?> { ["orderId"] = existing });

        if (reservation.Status != ReservationStatus.Active || !HoldPolicy.IsActive(now, reservation.ExpiresAt))
            throw ApiErrors.Conflict(ErrorCodes.HoldExpired, "This hold has expired; the reserved inventory is no longer guaranteed.",
                new Dictionary<string, object?> { ["expiresAt"] = reservation.ExpiresAt, ["serverTime"] = now });

        if (!psp.Value.IsConfigured) throw ApiErrors.Unavailable("Payments are not configured on this installation.");

        var session = await db.Sessions.FromSql($"SELECT * FROM catalog.sessions WHERE id = {reservation.SessionId} FOR SHARE").AsNoTracking().SingleAsync(ct);
        if (session.Status == SessionStatus.Cancelled)
            throw ApiErrors.Conflict(ErrorCodes.SessionCancelled, "This session was cancelled.");

        var method = request.PaymentMethod ?? "";
        var scenarioAllowed = demo.Value.SimulatorEnabled && session.DemoSimulatorEnabled;
        if (!PaymentMethods.All.Contains(method) || (method != PaymentMethods.Success && !scenarioAllowed))
            throw ApiErrors.Unprocessable(ErrorCodes.PaymentMethodNotAllowed,
                "This payment method is not available. Demo payment scenarios require the session's demo simulator to be enabled.");

        var order = new Order
        {
            Id = Guid.CreateVersion7(), ReservationId = reservation.Id, UserId = userId, SessionId = reservation.SessionId,
            OrganizationId = session.OrganizationId, OrderNumber = NewOrderNumber(), Status = OrderStatus.PendingPayment,
            TotalMinor = reservation.TotalMinor, Currency = reservation.Currency, CreatedAt = now,
        };
        var payment = new Payment
        {
            Id = Guid.CreateVersion7(), OrderId = order.Id, MerchantReference = $"pay_{order.Id:N}", Status = PaymentStatus.Initiated,
            AmountMinor = order.TotalMinor, Currency = order.Currency, MethodToken = method, CreatedAt = now, UpdatedAt = now,
            // Güvenlik ağı: başlatma mesajı hiç işlenmezse reconciliation devralır.
            NextReconcileAt = now.AddSeconds(Math.Max(30, psp.Value.ReconcileIntervalSeconds * 2)),
        };
        order.Payment = payment;
        db.Orders.Add(order);
        outbox.Enqueue(now, OutboxTypes.PaymentInitiate, new PaymentInitiatePayload(payment.Id));
        audit.Add(now, "order.checkout_started", "order", order.Id, order.OrganizationId, new { totalMinor = order.TotalMinor, ticketCount = reservation.TicketCount },
            actorKind: actingUserId is null ? "User" : "Demo", actorUserId: userId);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (TransactionRunner.IsUniqueViolation(e, OrdersModel.OrderPerReservationIndex))
        {
            // Son savunma: kilit atlanmış olsa bile veritabanı ikinci siparişi reddeder.
            throw ApiErrors.Conflict(ErrorCodes.OrderAlreadyExists, "A checkout was already started for this hold.");
        }

        var dto = await LoadAsync(order.Id, userId, now, ct) ?? throw new InvalidOperationException("Order vanished within its own transaction.");
        return new IdempotentResult(StatusCodes.Status202Accepted, dto with { Note = AcceptedNote }, $"/api/orders/{order.Id}");
    }

    public async Task<OrderDto?> LoadAsync(Guid orderId, Guid? ownerUserId, DateTimeOffset now, CancellationToken ct)
    {
        var row = await db.Orders.AsNoTracking()
            .Where(o => o.Id == orderId && (ownerUserId == null || o.UserId == ownerUserId))
            .Select(o => new
            {
                Order = o, o.Payment, Refund = o.Payment!.Refund,
                Session = db.Sessions.Where(s => s.Id == o.SessionId).Select(s => new { s.StartsAt, s.Timezone, s.City, s.Status, EventTitle = s.Event.Title, VenueName = s.Venue.Name, s.Event.IsDemo }).First(),
                Reservation = db.Reservations.Where(r => r.Id == o.ReservationId).Select(r => new { r.ExpiresAt, r.TicketCount }).First(),
            })
            .FirstOrDefaultAsync(ct);
        if (row is null) return null;

        var lines = await db.ReservationLines.AsNoTracking().Where(l => l.ReservationId == row.Order.ReservationId).OrderBy(l => l.LineNo)
            .Select(l => new HoldLineDto(l.LineNo, l.Kind, l.Label, l.Quantity, l.UnitPriceMinor, l.SessionSeatId, l.GaCategoryId)).ToListAsync(ct);

        var cutoff = policies.Value.CustomerCancelCutoff;
        var deadline = CancellationPolicy.CancelDeadline(row.Session.StartsAt, cutoff);
        var canCancel = row.Order.Status == OrderStatus.Paid && row.Session.Status != SessionStatus.Cancelled &&
                        CancellationPolicy.CustomerCanCancel(now, row.Session.StartsAt, cutoff);
        var payment = row.Payment!;

        return new OrderDto(row.Order.Id, row.Order.OrderNumber, row.Order.Status, row.Order.TotalMinor, row.Order.Currency, row.Order.CreatedAt,
            row.Order.PaidAt, row.Order.ClosedAt, row.Order.CancelReason, row.Order.ReservationId, row.Reservation.ExpiresAt, row.Order.SessionId,
            row.Session.EventTitle, row.Session.VenueName, row.Session.City, row.Session.StartsAt,
            OrganizerCatalogEndpoints.FormatLocal(row.Session.StartsAt, row.Session.Timezone), row.Session.Timezone, row.Session.Status,
            row.Reservation.TicketCount, lines,
            new OrderPaymentDto(payment.Status, payment.FailureKind, payment.FailureCode, payment.LateSuccessAt is not null),
            row.Refund is null ? null : new OrderRefundDto(row.Refund.Status, row.Refund.Reason, row.Refund.AmountMinor),
            canCancel, deadline, now, Note: "", IsDemo: row.Session.IsDemo);
    }

    private static string NewOrderNumber()
    {
        const string alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"; // Crockford base32: karışan harfler yok
        Span<byte> bytes = stackalloc byte[10];
        RandomNumberGenerator.Fill(bytes);
        Span<char> chars = stackalloc char[10];
        for (var i = 0; i < chars.Length; i++) chars[i] = alphabet[bytes[i] & 31];
        return "LC-" + new string(chars);
    }
}

internal static class OrderEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").RequireAuthorization().WithTags("Orders");

        api.MapPost("/holds/{holdId:guid}/checkout", async (Guid holdId, HttpContext http, IIdempotentEndpoint idempotent, OrderService orders,
                WorkerSignal<OutboxProcessor> signal) =>
            {
                var result = await idempotent.RunAsync<CheckoutRequest>(http, "order.checkout", (request, ct) => orders.CheckoutAsync(holdId, request, ct));
                signal.Notify(); // Commit sonrası: ödeme başlatma mesajı yoklama aralığını beklemeden işlensin.
                return result;
            })
            .RequireRateLimiting(RateLimitPolicies.Checkout);

        api.MapGet("/orders", async (HttpContext http, AppDbContext db, ICurrentUser user, OrderService orders, IClock clock, CancellationToken ct) =>
        {
            var userId = user.RequireUserId();
            var now = await clock.NowAsync(ct);
            var ids = await db.Orders.AsNoTracking().Where(o => o.UserId == userId).OrderByDescending(o => o.CreatedAt).ThenBy(o => o.Id)
                .Select(o => o.Id).Take(100).ToListAsync(ct);
            var items = new List<OrderDto>(ids.Count);
            foreach (var id in ids)
                if (await orders.LoadAsync(id, userId, now, ct) is { } dto) items.Add(dto);
            http.Response.Headers.CacheControl = "no-store";
            return Results.Ok(items);
        });

        api.MapGet("/orders/{orderId:guid}", async (Guid orderId, HttpContext http, ICurrentUser user, OrderService orders, IClock clock, CancellationToken ct) =>
        {
            // Sahiplik sorgunun parçasıdır: başkasının siparişi "yok" olarak görünür.
            var dto = await orders.LoadAsync(orderId, user.RequireUserId(), await clock.NowAsync(ct), ct) ?? throw ApiErrors.NotFound();
            http.Response.Headers.CacheControl = "no-store";
            return Results.Ok(dto);
        });

        var organizer = app.MapGroup("/api/organizer").RequireAuthorization().WithTags("Operations");
        organizer.MapPost("/payments/{paymentId:guid}/reconcile", ReconcilePaymentAsync);
        organizer.MapPost("/refunds/{refundId:guid}/retry", RetryRefundAsync);
    }

    /// <summary>Çağıran, kaynağın organizasyonunda yönetici veya platform yöneticisi olmalıdır.</summary>
    private static async Task RequireOperatorAsync(Guid organizationId, ICurrentUser user, IOrganizationAccess access, CancellationToken ct)
    {
        user.RequireUserId();
        if (user.IsPlatformAdmin) return;
        await access.RequireAdminAsync(organizationId, ct);
    }

    private static async Task<IResult> ReconcilePaymentAsync(Guid paymentId, AppDbContext db, ICurrentUser user, IOrganizationAccess access,
        PaymentService payments, IClock clock, IAuditWriter audit, AppMetrics metrics, CancellationToken ct)
    {
        var organizationId = await db.Payments.AsNoTracking().Where(p => p.Id == paymentId).Select(p => (Guid?)p.Order.OrganizationId).FirstOrDefaultAsync(ct)
            ?? throw ApiErrors.NotFound();
        await RequireOperatorAsync(organizationId, user, access, ct);

        // 1) Planlandı: niyet, denemeden önce kalıcı olarak kaydedilir.
        var now = await clock.NowAsync(ct);
        var run = new ReconciliationRun
        {
            Id = Guid.CreateVersion7(), TargetType = "payment", TargetId = paymentId, OrganizationId = organizationId, RequestedBy = user.UserId,
            Outcome = ReconciliationOutcome.Planned, DetailCode = "planned", CreatedAt = now, UpdatedAt = now,
        };
        db.ReconciliationRuns.Add(run);
        audit.Add(now, "payment.reconcile_requested", "payment", paymentId, organizationId);
        await db.SaveChangesAsync(ct);

        // 2) Denendi → 3) Başarılı / Başarısız. Başarısız deneme "onarım başarılı" olarak raporlanmaz.
        var result = await payments.ReconcileAsync(paymentId, ct);
        return Results.Ok(await FinishRunAsync(db, clock, metrics, run, result.Outcome, result.DetailCode, result.Attempted, ct));
    }

    private static async Task<IResult> RetryRefundAsync(Guid refundId, AppDbContext db, ICurrentUser user, IOrganizationAccess access,
        PaymentService payments, ITransactionRunner transactions, IClock clock, IAuditWriter audit, AppMetrics metrics, CancellationToken ct)
    {
        var target = await db.Refunds.AsNoTracking().Where(r => r.Id == refundId)
            .Select(r => new { r.PaymentId, r.Payment.Order.OrganizationId }).FirstOrDefaultAsync(ct) ?? throw ApiErrors.NotFound();
        await RequireOperatorAsync(target.OrganizationId, user, access, ct);

        var run = await transactions.RunAsync(async token =>
        {
            var aggregate = await payments.LockAggregateAsync(target.PaymentId, token);
            var refund = aggregate?.Payment.Refund ?? throw ApiErrors.NotFound();
            if (refund.Status != RefundStatus.Failed)
                throw ApiErrors.Conflict(ErrorCodes.RefundNotRetryable, $"Only failed refunds can be retried; this refund is {refund.Status}.");
            var now = await clock.NowAsync(token);
            refund.Status = RefundStatus.Pending;
            refund.LastErrorCode = null;
            refund.UpdatedAt = now;
            var created = new ReconciliationRun
            {
                Id = Guid.CreateVersion7(), TargetType = "refund", TargetId = refundId, OrganizationId = target.OrganizationId, RequestedBy = user.UserId,
                Outcome = ReconciliationOutcome.Planned, DetailCode = "planned", CreatedAt = now, UpdatedAt = now,
            };
            db.ReconciliationRuns.Add(created);
            audit.Add(now, "refund.retry_requested", "refund", refundId, target.OrganizationId);
            await db.SaveChangesAsync(token);
            return created;
        }, ct);
        db.ChangeTracker.Clear();

        ReconciliationOutcome outcome;
        string detail;
        try
        {
            var status = await payments.DispatchRefundAsync(refundId, ct);
            (outcome, detail) = status == RefundStatus.Succeeded
                ? (ReconciliationOutcome.Succeeded, "refund_succeeded")
                : (ReconciliationOutcome.Failed, "refund_failed");
        }
        catch (ProviderUnavailableException e)
        {
            // Deneme yapıldı ama sonuç alınamadı: iade yeniden "Failed" olur ki kuyrukta görünür kalsın.
            await payments.ResolveRefundAsync(target.PaymentId, RefundStatus.Failed, null, $"provider_unavailable:{e.ErrorCode}", ct);
            (outcome, detail) = (ReconciliationOutcome.Failed, $"provider_unavailable:{e.ErrorCode}");
        }
        db.ChangeTracker.Clear();
        return Results.Ok(await FinishRunAsync(db, clock, metrics, run, outcome, detail, attempted: true, ct));
    }

    private static async Task<OperatorRunDto> FinishRunAsync(AppDbContext db, IClock clock, AppMetrics metrics, ReconciliationRun run,
        ReconciliationOutcome outcome, string detail, bool attempted, CancellationToken ct)
    {
        metrics.Increment("loca_operator_runs_total", ("type", run.TargetType), ("outcome", outcome.ToString().ToLowerInvariant()));
        var now = await clock.NowAsync(ct);
        var code = detail.Length <= 60 ? detail : detail[..60];
        await db.ReconciliationRuns.Where(r => r.Id == run.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(r => r.Outcome, outcome).SetProperty(r => r.DetailCode, code).SetProperty(r => r.UpdatedAt, now), ct);
        return new OperatorRunDto(run.Id, run.TargetType, run.TargetId, Planned: true, Attempted: attempted, outcome, code);
    }
}
