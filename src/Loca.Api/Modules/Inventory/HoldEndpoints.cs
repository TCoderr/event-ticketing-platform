using Loca.Api.Infrastructure.Auth;
using Loca.Api.Infrastructure.Http;
using Loca.Api.Infrastructure.Idempotency;
using Loca.Api.Infrastructure.Metrics;
using Loca.Api.Infrastructure.Persistence;
using Loca.Api.Infrastructure.RateLimiting;
using Loca.Api.Infrastructure.Time;
using Loca.Api.Infrastructure.Tx;
using Loca.Api.Modules.Catalog;
using Loca.Api.Modules.Operations;
using Loca.Domain;
using Microsoft.EntityFrameworkCore;

namespace Loca.Api.Modules.Inventory;

public sealed record HoldItemRequest(Guid CategoryId, int Quantity);
public sealed record CreateHoldRequest(IReadOnlyList<Guid>? SeatIds, IReadOnlyList<HoldItemRequest>? Items);
public sealed record HoldLineDto(int LineNo, ReservationLineKind Kind, string Label, int Quantity, long UnitPriceMinor, Guid? SeatId, Guid? CategoryId);
public sealed record HoldDto(Guid Id, Guid SessionId, string Status, DateTimeOffset ExpiresAt, DateTimeOffset ServerTime, int TicketCount,
    long TotalMinor, string Currency, IReadOnlyList<HoldLineDto> Lines, Guid? OrderId, string Guarantee);

public sealed class HoldService(
    AppDbContext db, ICurrentUser currentUser, IClock clock, InventoryService inventory, IAuditWriter audit)
{
    /// <summary>Yanıtın neyi garanti ettiğini açıkça söyleyen sabit: envanter <c>expiresAt</c>'e kadar ayrılmıştır; satın alma değildir.</summary>
    public const string Guarantee = "inventory_reserved_until_expiry_not_a_purchase";

    /// <summary>
    /// Hold oluşturur. Çağıranın (idempotency) transaction'ı içinde çalışır: envanter claim'i, fiyat
    /// snapshot'ı, rezervasyon, denetim kaydı ve idempotency yanıtı birlikte commit olur.
    /// </summary>
    /// <param name="actingUserId">HTTP isteği dışındaki çağıranlar (demo izleyici worker'ı) için; verilmezse oturumdaki kullanıcı.</param>
    public async Task<IdempotentResult> CreateAsync(Guid sessionId, CreateHoldRequest request, CancellationToken ct, Guid? actingUserId = null)
    {
        var userId = actingUserId ?? currentUser.RequireUserId();
        var seatIds = request.SeatIds;
        var items = request.Items;
        if ((seatIds is null) == (items is null))
            throw ApiErrors.Unprocessable(ErrorCodes.InvalidSelection, "Provide either seatIds (reserved seating) or items (general admission), not both.");

        var ticketCount = seatIds?.Count ?? items!.Sum(i => (long)Math.Max(0, i.Quantity));
        var distinct = seatIds is not null ? seatIds.Distinct().Count() == seatIds.Count : items!.Select(i => i.CategoryId).Distinct().Count() == items!.Count;
        if (!distinct || (items is not null && items.Any(i => i.Quantity < 1)) || ticketCount < 1 || ticketCount > HoldPolicy.MaxTicketsPerHold)
            throw ApiErrors.Unprocessable(ErrorCodes.InvalidSelection,
                $"A hold contains between 1 and {HoldPolicy.MaxTicketsPerHold} tickets for a single session, without duplicates.");

        // Kilit sırası: (1) kullanıcının bu seanstaki aktif rezervasyonu, (2) seans, (3) envanter.
        var mine = await db.Reservations.FromSql($"""
            SELECT * FROM inventory.reservations
            WHERE user_id = {userId} AND session_id = {sessionId} AND status = 'Active' FOR UPDATE
            """).SingleOrDefaultAsync(ct);

        // FOR SHARE: seans iptali ve fiyat/plan değişikliği (FOR UPDATE) ile yarışmaz, sırayla çalışır.
        var session = await db.Sessions.FromSql($"SELECT * FROM catalog.sessions WHERE id = {sessionId} FOR SHARE").AsNoTracking().SingleOrDefaultAsync(ct)
            ?? throw ApiErrors.NotFound();

        var now = await clock.NowAsync(ct);

        // İptal edilmiş seans her şeyden önce bildirilir (kullanıcının bu seansta aktif hold'u olsa bile).
        if (session.Status == SessionStatus.Cancelled)
            throw ApiErrors.Conflict(ErrorCodes.SessionCancelled, "This session was cancelled.");

        // Süresi dolmuş kendi hold'u (worker henüz kapatmadıysa) claim sırasında, envanter kilitleri alındıktan sonra kapatılır.
        if (mine is not null && HoldPolicy.IsActive(now, mine.ExpiresAt))
            throw ApiErrors.Conflict(ErrorCodes.ActiveHoldExists, "You already have an active hold for this session.",
                new Dictionary<string, object?> { ["holdId"] = mine.Id, ["expiresAt"] = mine.ExpiresAt });

        await EnsureOnSaleAsync(session, now, ct);
        if ((session.SalesMode == SalesMode.ReservedSeating) != (seatIds is not null))
            throw ApiErrors.Unprocessable(ErrorCodes.InvalidSelection, session.SalesMode == SalesMode.ReservedSeating
                ? "This session uses reserved seating; select seats."
                : "This session is general admission; select ticket categories.");

        var reservation = new Reservation
        {
            Id = Guid.CreateVersion7(), SessionId = sessionId, UserId = userId, Status = ReservationStatus.Active, CreatedAt = now,
            ExpiresAt = HoldPolicy.ExpiresAt(now, session.HoldTtlSeconds), TicketCount = (int)ticketCount, Currency = Money.SupportedCurrency,
        };

        var lines = seatIds is not null
            ? await inventory.ClaimSeatsAsync(reservation, seatIds, now, mine, ct)
            : await inventory.ClaimGeneralAdmissionAsync(reservation, [.. items!.Select(i => new GaSelection(i.CategoryId, i.Quantity))], now, mine, ct);

        var total = Money.Zero;
        foreach (var line in lines) total = total.Add(Money.FromMinor(line.UnitPriceMinor).Multiply(line.Quantity));
        reservation.TotalMinor = total.Minor;
        reservation.Lines = lines;
        db.Reservations.Add(reservation);
        audit.Add(now, "hold.created", "reservation", reservation.Id, session.OrganizationId,
            new { sessionId, ticketCount = reservation.TicketCount, totalMinor = reservation.TotalMinor, mode = session.SalesMode.ToString() },
            actorKind: actingUserId is null ? "User" : "Demo", actorUserId: userId);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (TransactionRunner.IsUniqueViolation(e, ReservationModel.ActiveHoldPerUserIndex))
        {
            // Aynı kullanıcının eşzamanlı ikinci isteği: transaction zaten geri alınacak, başka sorgu yapılmaz.
            throw ApiErrors.Conflict(ErrorCodes.ActiveHoldExists, "You already have an active hold for this session.");
        }

        return new IdempotentResult(StatusCodes.Status201Created, ToDto(reservation, lines, now, orderId: null), $"/api/holds/{reservation.Id}");
    }

    private async Task EnsureOnSaleAsync(Session session, DateTimeOffset now, CancellationToken ct)
    {
        if (session.Status == SessionStatus.Cancelled)
            throw ApiErrors.Conflict(ErrorCodes.SessionCancelled, "This session was cancelled.");
        var visible = session.Status == SessionStatus.Published && await db.Events.AsNoTracking().AnyAsync(e =>
            e.Id == session.EventId && e.Status == EventStatus.Published && e.ModerationBlockedAt == null &&
            e.Organization.Status == OrganizationStatus.Active, ct);
        if (!visible) throw ApiErrors.NotFound();

        switch (SalesWindow.Evaluate(now, session.SalesStartAt, session.SalesEndAt))
        {
            case SalesWindowState.NotStarted:
                throw ApiErrors.Conflict(ErrorCodes.SalesNotStarted, "Sales have not started yet.",
                    new Dictionary<string, object?> { ["salesStartAt"] = session.SalesStartAt, ["serverTime"] = now });
            case SalesWindowState.Closed:
                throw ApiErrors.Conflict(ErrorCodes.SalesClosed, "Sales are closed for this session.",
                    new Dictionary<string, object?> { ["salesEndAt"] = session.SalesEndAt, ["serverTime"] = now });
        }
    }

    internal static HoldDto ToDto(Reservation reservation, IEnumerable<ReservationLine> lines, DateTimeOffset now, Guid? orderId) =>
        new(reservation.Id, reservation.SessionId, EffectiveStatus(reservation, now), reservation.ExpiresAt, now, reservation.TicketCount,
            reservation.TotalMinor, reservation.Currency,
            [.. lines.OrderBy(l => l.LineNo).Select(l => new HoldLineDto(l.LineNo, l.Kind, l.Label, l.Quantity, l.UnitPriceMinor, l.SessionSeatId, l.GaCategoryId))],
            orderId, Guarantee);

    /// <summary>Worker gecikmiş olsa bile süresi dolmuş hold "Active" olarak raporlanmaz.</summary>
    internal static string EffectiveStatus(Reservation reservation, DateTimeOffset now) =>
        reservation.Status == ReservationStatus.Active && !HoldPolicy.IsActive(now, reservation.ExpiresAt)
            ? nameof(ReservationStatus.Expired)
            : reservation.Status.ToString();
}

internal static class HoldEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").RequireAuthorization().WithTags("Holds");

        api.MapPost("/sessions/{sessionId:guid}/holds", async (Guid sessionId, HttpContext http, IIdempotentEndpoint idempotent, HoldService holds, AppMetrics metrics) =>
            {
                try
                {
                    var result = await idempotent.RunAsync<CreateHoldRequest>(http, "hold.create", (request, ct) => holds.CreateAsync(sessionId, request, ct));
                    metrics.Increment("loca_holds_total", ("outcome", "created"));
                    return result;
                }
                catch (ApiException e)
                {
                    metrics.Increment("loca_holds_total", ("outcome", e.Code)); // sabit hata kodu kümesi: düşük kardinalite
                    throw;
                }
            })
            .RequireRateLimiting(RateLimitPolicies.Hold);

        api.MapGet("/sessions/{sessionId:guid}/my-hold", async (Guid sessionId, HttpContext http, AppDbContext db, ICurrentUser user, IClock clock,
            IHoldOrderLookup orders, CancellationToken ct) =>
        {
            var userId = user.RequireUserId();
            var now = await clock.NowAsync(ct);
            var reservation = await db.Reservations.AsNoTracking().Include(r => r.Lines)
                .FirstOrDefaultAsync(r => r.UserId == userId && r.SessionId == sessionId && r.Status == ReservationStatus.Active && r.ExpiresAt > now, ct);
            http.Response.Headers.CacheControl = "no-store";
            return reservation is null
                ? Results.NoContent()
                : Results.Ok(HoldService.ToDto(reservation, reservation.Lines, now, await orders.FindOrderIdAsync(reservation.Id, ct)));
        });

        api.MapGet("/holds/{holdId:guid}", async (Guid holdId, HttpContext http, AppDbContext db, ICurrentUser user, IClock clock,
            IHoldOrderLookup orders, CancellationToken ct) =>
        {
            var userId = user.RequireUserId();
            // Sahiplik sorgunun parçasıdır: başkasının hold'u "yok" olarak görünür.
            var reservation = await db.Reservations.AsNoTracking().Include(r => r.Lines)
                .FirstOrDefaultAsync(r => r.Id == holdId && r.UserId == userId, ct) ?? throw ApiErrors.NotFound();
            http.Response.Headers.CacheControl = "no-store";
            return Results.Ok(HoldService.ToDto(reservation, reservation.Lines, await clock.NowAsync(ct), await orders.FindOrderIdAsync(reservation.Id, ct)));
        });

        api.MapDelete("/holds/{holdId:guid}", async (Guid holdId, AppDbContext db, ICurrentUser user, IClock clock, ITransactionRunner transactions,
            InventoryService inventory, IHoldOrderLookup orders, IAuditWriter audit, CancellationToken ct) =>
        {
            var userId = user.RequireUserId();
            await transactions.RunAsync(async token =>
            {
                var reservation = await inventory.LockReservationAsync(holdId, token);
                if (reservation is null || reservation.UserId != userId) throw ApiErrors.NotFound();
                var now = await clock.NowAsync(token);
                if (reservation.Status != ReservationStatus.Active || !HoldPolicy.IsActive(now, reservation.ExpiresAt))
                    throw ApiErrors.Conflict(ErrorCodes.HoldNotActive, "This hold is no longer active.");
                if (await orders.FindOrderIdAsync(reservation.Id, token) is not null)
                    throw ApiErrors.Conflict(ErrorCodes.OrderAlreadyExists, "A checkout was already started for this hold; it can no longer be released.");

                await inventory.CloseWithoutSaleAsync(reservation, ReservationStatus.Released, "CustomerReleased", now, token);
                audit.Add(now, "hold.released", "reservation", reservation.Id);
                await db.SaveChangesAsync(token);
                return 0;
            }, ct);
            return Results.NoContent();
        });
    }
}

/// <summary>Sipariş modülünün sağladığı dar sorgu: bir hold için sipariş açılmış mı?</summary>
public interface IHoldOrderLookup
{
    Task<Guid?> FindOrderIdAsync(Guid reservationId, CancellationToken ct);
}

internal sealed class NoOrdersYet : IHoldOrderLookup
{
    public Task<Guid?> FindOrderIdAsync(Guid reservationId, CancellationToken ct) => Task.FromResult<Guid?>(null);
}
