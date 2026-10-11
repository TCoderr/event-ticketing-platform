using Loca.Api.Infrastructure.Auth;
using Loca.Api.Infrastructure.Config;
using Loca.Api.Infrastructure.Http;
using Loca.Api.Infrastructure.Idempotency;
using Loca.Api.Infrastructure.Outbox;
using Loca.Api.Infrastructure.Persistence;
using Loca.Api.Infrastructure.RateLimiting;
using Loca.Api.Infrastructure.Time;
using Loca.Api.Infrastructure.Tx;
using Loca.Api.Infrastructure.Workers;
using Loca.Api.Modules.Catalog;
using Loca.Api.Modules.Inventory;
using Loca.Api.Modules.Operations;
using Loca.Api.Modules.Tickets;
using Loca.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Loca.Api.Modules.Orders;

/// <summary>Seans iptalinin kalıcı toplu işi. Rezervasyonlar imleç sırasıyla, her biri kendi transaction'ında işlenir.</summary>
public sealed class SessionCancellationJob
{
    public Guid SessionId { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid RequestedBy { get; set; }
    public CancellationJobStatus Status { get; set; }

    /// <summary>İşlenen son rezervasyon. Yeniden başlatmada buradan devam edilir.</summary>
    public Guid? CursorReservationId { get; set; }

    public int ProcessedCount { get; set; }
    public int RefundCount { get; set; }
    public string? LeaseOwner { get; set; }
    public Guid? LeaseToken { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

internal static class CancellationModel
{
    public static void Configure(ModelBuilder b)
    {
        b.Entity<SessionCancellationJob>(e =>
        {
            e.ToTable("session_cancellation_jobs", OrdersModel.Schema, t =>
                t.HasCheckConstraint("ck_session_cancellation_jobs_status", "status IN ('Pending','Completed')"));
            e.HasKey(x => x.SessionId);
            e.Property(x => x.SessionId).ValueGeneratedNever();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(12);
            e.Property(x => x.LeaseOwner).HasMaxLength(80);
            e.HasOne<Session>().WithMany().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.Status).HasFilter("status = 'Pending'");
        });
    }
}

public sealed class CancellationOptions
{
    public const string Section = "Cancellation";
    public int BatchSize { get; set; } = 25;
    public int LeaseSeconds { get; set; } = 60;
}

public sealed record SessionCancellationDto(Guid SessionId, SessionStatus Status, CancellationJobStatus JobStatus, int ProcessedCount, int RefundCount);

public sealed class CancellationService(
    AppDbContext db, ICurrentUser currentUser, IClock clock, InventoryService inventory, TicketService tickets, PaymentService payments,
    OrderService orders, IAuditWriter audit, IOptions<PolicyOptions> policies, IFailpoints failpoints)
{
    /// <summary>
    /// Müşterinin tam sipariş iptali. İdempotency transaction'ı içinde çalışır ve check-in ile aynı kilit kökünü
    /// (rezervasyon) kullanır: hem kabul edilmiş giriş hem de iade aynı sipariş için oluşamaz.
    /// Bilet, bu commit ile birlikte geçersizdir; iadenin sonucu beklenmez. Envanter aynı transaction'da yeniden açılır.
    /// </summary>
    public async Task<IdempotentResult> CancelByCustomerAsync(Guid orderId, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        var reservationId = await db.Orders.AsNoTracking().Where(o => o.Id == orderId && o.UserId == userId).Select(o => (Guid?)o.ReservationId).FirstOrDefaultAsync(ct)
            ?? throw ApiErrors.NotFound();

        var reservation = await inventory.LockReservationAsync(reservationId, ct) ?? throw ApiErrors.NotFound();
        var order = await db.Orders.Include(o => o.Payment).FirstAsync(o => o.Id == orderId, ct);
        var session = await db.Sessions.FromSql($"SELECT * FROM catalog.sessions WHERE id = {order.SessionId} FOR SHARE").AsNoTracking().SingleAsync(ct);
        var now = await clock.NowAsync(ct);

        if (order.Status != OrderStatus.Paid)
            throw ApiErrors.Conflict(ErrorCodes.OrderNotPaid, $"Only paid orders can be cancelled; this order is {order.Status}.");
        if (session.Status == SessionStatus.Cancelled)
            throw ApiErrors.Conflict(ErrorCodes.SessionCancelled, "The session was cancelled; this order is being refunded automatically.");

        var cutoff = policies.Value.CustomerCancelCutoff;
        if (!CancellationPolicy.CustomerCanCancel(now, session.StartsAt, cutoff))
            throw ApiErrors.Conflict(ErrorCodes.CancellationWindowClosed, "The cancellation window for this session has closed.",
                new Dictionary<string, object?> { ["cancelDeadline"] = CancellationPolicy.CancelDeadline(session.StartsAt, cutoff), ["serverTime"] = now });
        if (await tickets.AnyCheckedInAsync(order.Id, ct))
            throw ApiErrors.Conflict(ErrorCodes.OrderHasCheckedInTicket, "An order with a used ticket cannot be cancelled.");

        order.Status = OrderStatus.Cancelled;
        order.CancelReason = OrderCancelReason.Customer;
        order.ClosedAt = now;
        var voided = await tickets.VoidForOrderAsync(order.Id, "CustomerCancellation", includeCheckedIn: false, now, ct);
        await inventory.ReopenSoldAsync(reservation, ct); // Seans satışta: envanter bu transaction'da yeniden açılır.
        payments.AddRefund(order.Payment!, order, RefundReason.CustomerCancellation, now);
        audit.Add(now, "order.cancelled_by_customer", "order", order.Id, order.OrganizationId, new { voidedTickets = voided, refundMinor = order.TotalMinor });
        await db.SaveChangesAsync(ct);
        await failpoints.HitAsync("order.cancel.before-commit", ct);

        var dto = await orders.LoadAsync(order.Id, userId, now, ct) ?? throw new InvalidOperationException("Order vanished within its own transaction.");
        return new IdempotentResult(StatusCodes.Status200OK, dto);
    }

    /// <summary>
    /// Organizatörün seans iptali: seansı kalıcı olarak kapatır ve toplu iade işini kaydeder. İptal edilen seans
    /// yeniden satışa açılmaz. Siparişlerin tek tek iptali <see cref="SessionCancellationProcessor"/> tarafından yapılır.
    /// </summary>
    public async Task<IdempotentResult> CancelSessionAsync(Guid sessionId, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        // FOR UPDATE: süren hold istekleri (FOR SHARE) bitene kadar bekler; commit'ten sonra yeni hold oluşamaz.
        var session = await OrganizerCatalogEndpoints.LockSessionAsync(db, sessionId, ct);
        StateMachines.Ensure(StateMachines.CanTransition(session.Status, SessionStatus.Cancelled), session.Status, SessionStatus.Cancelled);

        var now = await clock.NowAsync(ct);
        session.Status = SessionStatus.Cancelled;
        session.CancelledAt = now;
        var job = new SessionCancellationJob
        {
            SessionId = sessionId, OrganizationId = session.OrganizationId, RequestedBy = userId, Status = CancellationJobStatus.Pending, CreatedAt = now,
        };
        db.SessionCancellationJobs.Add(job);
        audit.Add(now, "session.cancelled", "session", sessionId, session.OrganizationId);
        await db.SaveChangesAsync(ct);
        return new IdempotentResult(StatusCodes.Status202Accepted,
            new SessionCancellationDto(sessionId, session.Status, job.Status, job.ProcessedCount, job.RefundCount));
    }
}

/// <summary>
/// Seans iptal işlerini yürütür. Sahiplik lease + fencing token ile; ilerleme (imleç) her rezervasyonla aynı
/// transaction'da ve yalnız token hâlâ geçerliyse yazılır. Çöken sahibin işi lease dolunca imleçten devralınır;
/// her adım durum kontrollü olduğundan yeniden işleme ikinci etki üretmez.
/// </summary>
public sealed class SessionCancellationProcessor(
    AppDbContext db, IClock clock, ITransactionRunner transactions, InventoryService inventory, TicketService tickets, PaymentService payments,
    IAuditWriter audit, IOptions<CancellationOptions> options, IFailpoints failpoints, WorkerSignal<OutboxProcessor> outboxSignal) : IBatchProcessor
{
    private readonly string _owner = $"{Environment.MachineName}:{Guid.NewGuid():N}";

    public string Name => "session-cancellation";

    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var job = await ClaimAsync(ct);
        if (job is null) return 0;
        var processed = 0;
        while (!ct.IsCancellationRequested)
        {
            var cursor = await db.SessionCancellationJobs.AsNoTracking().Where(j => j.SessionId == job.SessionId).Select(j => j.CursorReservationId).FirstAsync(ct);
            var batch = await db.Reservations.AsNoTracking()
                .Where(r => r.SessionId == job.SessionId && (cursor == null || r.Id > cursor))
                .OrderBy(r => r.Id).Select(r => r.Id).Take(options.Value.BatchSize).ToListAsync(ct);
            if (batch.Count == 0)
            {
                await CompleteAsync(job, ct);
                break;
            }
            foreach (var reservationId in batch)
            {
                if (!await ProcessReservationAsync(job, reservationId, ct)) return processed; // Lease kaybedildi: dur.
                processed++;
                await failpoints.HitAsync("session-cancel.after-item", ct);
            }
            await RenewAsync(job, ct);
        }
        outboxSignal.Notify();
        return processed;
    }

    public async Task<SessionCancellationJob?> ClaimAsync(CancellationToken ct, string? ownerOverride = null)
    {
        var now = await clock.NowAsync(ct);
        var leaseUntil = now.AddSeconds(options.Value.LeaseSeconds);
        var owner = ownerOverride ?? _owner;
        var claimed = await db.SessionCancellationJobs.FromSql($"""
            UPDATE orders.session_cancellation_jobs AS j
            SET lease_owner = {owner}, lease_token = gen_random_uuid(), lease_until = {leaseUntil}
            WHERE j.session_id = (
                SELECT session_id FROM orders.session_cancellation_jobs
                WHERE status = 'Pending' AND (lease_until IS NULL OR lease_until <= {now})
                ORDER BY created_at, session_id LIMIT 1 FOR UPDATE SKIP LOCKED)
            RETURNING j.*
            """).AsNoTracking().ToListAsync(ct);
        return claimed.SingleOrDefault();
    }

    /// <returns>Lease artık bu sahibe ait değilse <c>false</c>; hiçbir değişiklik commit edilmez.</returns>
    public async Task<bool> ProcessReservationAsync(SessionCancellationJob job, Guid reservationId, CancellationToken ct)
    {
        try
        {
            await transactions.RunAsync(token => ProcessInTransactionAsync(job, reservationId, token), ct);
            return true;
        }
        catch (LeaseLostException)
        {
            return false; // Transaction geri alındı: bu sahibin yaptığı hiçbir değişiklik kalıcı olmadı.
        }
    }

    private async Task<int> ProcessInTransactionAsync(SessionCancellationJob job, Guid reservationId, CancellationToken token)
    {
        var reservation = await inventory.LockReservationAsync(reservationId, token);
        if (reservation is null) return 0;
        var now = await clock.NowAsync(token);
        var order = await db.Orders.Include(o => o.Payment).ThenInclude(p => p!.Refund).FirstOrDefaultAsync(o => o.ReservationId == reservationId, token);
        var refunded = 0;

        if (order is { Status: OrderStatus.Paid })
        {
            order.Status = OrderStatus.Cancelled;
            order.CancelReason = OrderCancelReason.SessionCancelled;
            order.ClosedAt = now;
            // Seans iptalinde kullanılmış biletler de geçersiz olur; envanter YENİDEN AÇILMAZ.
            var voided = await tickets.VoidForOrderAsync(order.Id, "SessionCancellation", includeCheckedIn: true, now, token);
            if (order.Payment is { Refund: null } payment)
            {
                payments.AddRefund(payment, order, RefundReason.SessionCancellation, now);
                refunded = 1;
            }
            audit.Add(now, "order.cancelled_by_session", "order", order.Id, order.OrganizationId, new { voidedTickets = voided }, actorKind: "Worker");
        }
        else if (order is { Status: OrderStatus.PendingPayment })
        {
            // Ödeme hâlâ sonuçlanmamış: sipariş kapanır. Ödeme sonradan başarıyla dönerse geç başarı telafisi iade üretir.
            order.Status = OrderStatus.Cancelled;
            order.CancelReason = OrderCancelReason.SessionCancelled;
            order.ClosedAt = now;
            audit.Add(now, "order.cancelled_by_session", "order", order.Id, order.OrganizationId, new { voidedTickets = 0 }, actorKind: "Worker");
        }

        if (reservation.Status == ReservationStatus.Active)
            await inventory.CloseWithoutSaleAsync(reservation, ReservationStatus.Released, "SessionCancelled", now, token);

        await db.SaveChangesAsync(token);

        // Fencing: ilerleme yalnız lease hâlâ bu sahibe aitse yazılır. Değilse transaction geri alınır.
        var advanced = await db.SessionCancellationJobs
            .Where(j => j.SessionId == job.SessionId && j.LeaseToken == job.LeaseToken && j.Status == CancellationJobStatus.Pending)
            .ExecuteUpdateAsync(u => u
                .SetProperty(j => j.CursorReservationId, reservationId)
                .SetProperty(j => j.ProcessedCount, j => j.ProcessedCount + 1)
                .SetProperty(j => j.RefundCount, j => j.RefundCount + refunded), token);
        if (advanced != 1) throw new LeaseLostException();
        return 1;
    }

    private async Task RenewAsync(SessionCancellationJob job, CancellationToken ct)
    {
        var leaseUntil = (await clock.NowAsync(ct)).AddSeconds(options.Value.LeaseSeconds);
        await db.SessionCancellationJobs.Where(j => j.SessionId == job.SessionId && j.LeaseToken == job.LeaseToken)
            .ExecuteUpdateAsync(u => u.SetProperty(j => j.LeaseUntil, leaseUntil), ct);
    }

    public async Task<bool> CompleteAsync(SessionCancellationJob job, CancellationToken ct)
    {
        var now = await clock.NowAsync(ct);
        return await db.SessionCancellationJobs
            .Where(j => j.SessionId == job.SessionId && j.LeaseToken == job.LeaseToken && j.Status == CancellationJobStatus.Pending)
            .ExecuteUpdateAsync(u => u
                .SetProperty(j => j.Status, CancellationJobStatus.Completed)
                .SetProperty(j => j.CompletedAt, now)
                .SetProperty(j => j.LeaseOwner, (string?)null)
                .SetProperty(j => j.LeaseUntil, (DateTimeOffset?)null), ct) == 1;
    }

    private sealed class LeaseLostException : Exception;
}

internal static class CancellationEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/orders/{orderId:guid}/cancel", async (Guid orderId, HttpContext http, IIdempotentEndpoint idempotent, CancellationService cancellations,
                WorkerSignal<OutboxProcessor> signal) =>
            {
                var result = await idempotent.RunAsync<NoBody>(http, "order.cancel", (_, ct) => cancellations.CancelByCustomerAsync(orderId, ct));
                signal.Notify();
                return result;
            })
            .RequireAuthorization().RequireRateLimiting(RateLimitPolicies.Checkout).WithTags("Orders");

        app.MapPost("/api/organizer/sessions/{sessionId:guid}/cancel", async (Guid sessionId, HttpContext http, IIdempotentEndpoint idempotent,
                CancellationService cancellations, CatalogAccess access, WorkerSignal<SessionCancellationProcessor> signal) =>
            {
                var result = await idempotent.RunAsync<NoBody>(http, "session.cancel",
                    (_, ct) => cancellations.CancelSessionAsync(sessionId, ct),
                    authorize: async ct => await access.RequireSessionAdminAsync(sessionId, ct));
                signal.Notify();
                return result;
            })
            .RequireAuthorization().WithTags("Organizer");

        app.MapGet("/api/organizer/sessions/{sessionId:guid}/cancellation", async (Guid sessionId, AppDbContext db, CatalogAccess access, CancellationToken ct) =>
        {
            await access.RequireSessionAdminAsync(sessionId, ct);
            var dto = await db.SessionCancellationJobs.AsNoTracking().Where(j => j.SessionId == sessionId)
                .Select(j => new SessionCancellationDto(j.SessionId, SessionStatus.Cancelled, j.Status, j.ProcessedCount, j.RefundCount))
                .FirstOrDefaultAsync(ct) ?? throw ApiErrors.NotFound();
            return Results.Ok(dto);
        }).RequireAuthorization().WithTags("Organizer");
    }
}
