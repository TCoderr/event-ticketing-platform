using Loca.Api.Infrastructure.Auth;
using Loca.Api.Infrastructure.Config;
using Loca.Api.Infrastructure.Http;
using Loca.Api.Infrastructure.Idempotency;
using Loca.Api.Infrastructure.Metrics;
using Loca.Api.Infrastructure.Persistence;
using Loca.Api.Infrastructure.RateLimiting;
using Loca.Api.Infrastructure.Time;
using Loca.Api.Modules.Catalog;
using Loca.Api.Modules.Inventory;
using Loca.Api.Modules.Operations;
using Loca.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Loca.Api.Modules.Tickets;

public sealed record TicketListItem(Guid Id, Guid OrderId, Guid SessionId, string EventTitle, string VenueName, string City, DateTimeOffset StartsAt,
    string StartsAtLocal, string Timezone, string Label, TicketStatus Status, DateTimeOffset IssuedAt, DateTimeOffset? CheckedInAt, bool IsDemo);
public sealed record TicketDetail(Guid Id, Guid OrderId, Guid SessionId, string EventTitle, string VenueName, string VenueAddress, string City,
    DateTimeOffset StartsAt, string StartsAtLocal, string Timezone, string Label, TicketStatus Status, DateTimeOffset IssuedAt,
    DateTimeOffset? CheckedInAt, DateTimeOffset CheckInOpensAt, DateTimeOffset CheckInClosesAt, string? Token, DateTimeOffset ServerTime, bool IsDemo);
public sealed record CheckInRequest(string Token, string? Gate);
public sealed record CheckInResponse(string Result, Guid TicketId, string Label, DateTimeOffset CheckedInAt, string? Gate);
public sealed record GateSessionDto(Guid SessionId, string EventTitle, string VenueName, string City, DateTimeOffset StartsAt, string StartsAtLocal,
    string Timezone, SessionStatus Status, DateTimeOffset CheckInOpensAt, DateTimeOffset CheckInClosesAt, bool WindowOpen, int TicketsTotal,
    int TicketsCheckedIn, DateTimeOffset ServerTime);
public sealed record GateStaffDto(Guid UserId, string DisplayName, bool Assigned);

/// <summary>
/// Kapı yetkisi: kullanıcı hem seansa atanmış hem de seansın organizasyonunun hâlâ üyesi olmalıdır.
/// Her istekte veritabanından doğrulanır; atanmamış personel için seans "yok" görünür.
/// </summary>
public sealed class GateAccess(AppDbContext db, ICurrentUser currentUser)
{
    public async Task RequireAssignedAsync(Guid sessionId, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        var allowed = await db.GateAssignments.AsNoTracking().AnyAsync(a => a.SessionId == sessionId && a.UserId == userId &&
            db.Sessions.Any(s => s.Id == sessionId && db.OrganizationMembers.Any(m => m.OrganizationId == s.OrganizationId && m.UserId == userId)), ct);
        if (!allowed) throw ApiErrors.NotFound();
    }
}

public sealed class CheckInService(
    AppDbContext db, ICurrentUser currentUser, IClock clock, InventoryService inventory, IAuditWriter audit, IOptions<PolicyOptions> policies,
    IFailpoints failpoints)
{
    /// <summary>
    /// Atomik, tek kullanımlık check-in. İdempotency transaction'ı içinde çalışır. İptal ile aynı kilit kökünü
    /// (rezervasyon) kullanır: hem kabul edilmiş giriş hem de iptal/iade aynı bilet için oluşamaz.
    /// </summary>
    public async Task<IdempotentResult> CheckInAsync(Guid sessionId, CheckInRequest request, CancellationToken ct)
    {
        var staffId = currentUser.RequireUserId();
        var gate = string.IsNullOrWhiteSpace(request.Gate) ? null : request.Gate.Trim();
        if (gate is { Length: > 40 })
            throw ApiErrors.BadRequest(ErrorCodes.ValidationFailed, "Gate label is too long.");

        // Biçimi bozuk token veritabanına gitmeden reddedilir. Token hiçbir yere loglanmaz.
        if (!TicketToken.IsWellFormed(request.Token)) throw NotFound();
        var hash = TicketToken.Hash(request.Token);
        var found = await db.Tickets.AsNoTracking().Where(t => t.TokenHash == hash).Select(t => new { t.Id, t.SessionId, t.ReservationId }).FirstOrDefaultAsync(ct);
        if (found is null) throw NotFound();

        // Başka seansın bileti: o seans hakkında hiçbir bilgi verilmez.
        if (found.SessionId != sessionId)
            throw Reject(ErrorCodes.WrongSession, "This ticket is not valid for this session.", found.Id);

        await inventory.LockReservationAsync(found.ReservationId, ct); // Küme kökü.
        var ticket = await db.Tickets.FirstAsync(t => t.Id == found.Id, ct);
        var session = await db.Sessions.FromSql($"SELECT * FROM catalog.sessions WHERE id = {sessionId} FOR SHARE").AsNoTracking().SingleAsync(ct);
        var now = await clock.NowAsync(ct);

        if (session.Status == SessionStatus.Cancelled)
            throw Reject(ErrorCodes.SessionCancelled, "This session was cancelled.", ticket.Id);
        if (ticket.Status == TicketStatus.Cancelled)
            throw Reject(ErrorCodes.TicketVoid, "This ticket was cancelled and is no longer valid.", ticket.Id);
        if (ticket.Status == TicketStatus.CheckedIn)
            throw Reject(ErrorCodes.AlreadyCheckedIn, "This ticket was already used.", ticket.Id, new Dictionary<string, object?>
            {
                // İlk giriş bilgisi yalnız bu (yetkili) personel yanıtında yer alır.
                ["checkedInAt"] = ticket.CheckedInAt, ["gate"] = ticket.CheckedInGate, ["label"] = ticket.Label,
            });

        var policy = policies.Value;
        if (!CheckInWindow.IsOpen(now, session.StartsAt, policy.CheckInOpensBefore, policy.CheckInClosesAfter))
            throw Reject(ErrorCodes.OutsideCheckInWindow, "Check-in is not open for this session at this time.", ticket.Id, new Dictionary<string, object?>
            {
                ["opensAt"] = CheckInWindow.OpensAt(session.StartsAt, policy.CheckInOpensBefore),
                ["closesAt"] = CheckInWindow.ClosesAt(session.StartsAt, policy.CheckInClosesAfter),
                ["serverTime"] = now,
            });

        // Koşullu geçiş (compare-and-set): kilit olmasa bile aynı bileti iki istek kabul edemez.
        var accepted = await db.Tickets.Where(t => t.Id == ticket.Id && t.Status == TicketStatus.Valid)
            .ExecuteUpdateAsync(u => u
                .SetProperty(t => t.Status, TicketStatus.CheckedIn)
                .SetProperty(t => t.CheckedInAt, now)
                .SetProperty(t => t.CheckedInBy, staffId)
                .SetProperty(t => t.CheckedInGate, gate), ct);
        if (accepted != 1)
            throw Reject(ErrorCodes.AlreadyCheckedIn, "This ticket was already used.", ticket.Id);

        db.ScanLog.Add(new ScanLogEntry { SessionId = sessionId, TicketId = ticket.Id, StaffUserId = staffId, Result = "Accepted", Gate = gate, CreatedAt = now });
        audit.Add(now, "ticket.checked_in", "ticket", ticket.Id, session.OrganizationId, new { sessionId, gate });
        await db.SaveChangesAsync(ct);
        await failpoints.HitAsync("checkin.before-commit", ct);
        return new IdempotentResult(StatusCodes.Status200OK, new CheckInResponse("Accepted", ticket.Id, ticket.Label, now, gate));

        static ApiException NotFound() => new(StatusCodes.Status404NotFound, ErrorCodes.TicketNotFound, "No ticket matches this code.",
            new Dictionary<string, object?> { ["result"] = "Invalid" });
    }

    private static ApiException Reject(string code, string detail, Guid ticketId, Dictionary<string, object?>? extra = null)
    {
        var extensions = extra ?? [];
        extensions["result"] = code == ErrorCodes.AlreadyCheckedIn ? "AlreadyUsed" : "Invalid";
        extensions["ticketId"] = ticketId;
        return ApiErrors.Conflict(code, detail, extensions);
    }
}

internal static class TicketEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").RequireAuthorization().WithTags("Tickets");

        api.MapGet("/tickets", async (HttpContext http, AppDbContext db, ICurrentUser user, CancellationToken ct) =>
        {
            var userId = user.RequireUserId();
            var rows = await (from t in db.Tickets.AsNoTracking()
                              join s in db.Sessions on t.SessionId equals s.Id
                              where t.OwnerUserId == userId
                              orderby s.StartsAt, t.Label, t.Id
                              select new { t.Id, t.OrderId, t.SessionId, EventTitle = s.Event.Title, VenueName = s.Venue.Name, s.City, s.StartsAt, s.Timezone, t.Label, t.Status, t.IssuedAt, t.CheckedInAt, s.Event.IsDemo })
                .Take(200).ToListAsync(ct);
            http.Response.Headers.CacheControl = "no-store";
            // Liste token içermez; QR yalnız tek bilet ayrıntısında, sahibine verilir.
            return Results.Ok(rows.Select(r => new TicketListItem(r.Id, r.OrderId, r.SessionId, r.EventTitle, r.VenueName, r.City, r.StartsAt,
                OrganizerCatalogEndpoints.FormatLocal(r.StartsAt, r.Timezone), r.Timezone, r.Label, r.Status, r.IssuedAt, r.CheckedInAt, r.IsDemo)));
        });

        api.MapGet("/tickets/{ticketId:guid}", async (Guid ticketId, HttpContext http, AppDbContext db, ICurrentUser user, IClock clock,
            TicketTokenProtector protector, IOptions<PolicyOptions> policies, CancellationToken ct) =>
        {
            var userId = user.RequireUserId();
            var row = await (from t in db.Tickets.AsNoTracking()
                             join s in db.Sessions on t.SessionId equals s.Id
                             where t.Id == ticketId && t.OwnerUserId == userId
                             select new { Ticket = t, EventTitle = s.Event.Title, VenueName = s.Venue.Name, VenueAddress = s.Venue.Address, s.City, s.StartsAt, s.Timezone, s.Event.IsDemo })
                .FirstOrDefaultAsync(ct) ?? throw ApiErrors.NotFound();

            var policy = policies.Value;
            // Kullanılmış veya iptal edilmiş biletin QR'ı gösterilmez.
            var token = row.Ticket.Status == TicketStatus.Valid ? protector.Unprotect(row.Ticket.TokenProtected) : null;
            http.Response.Headers.CacheControl = "no-store";
            http.Response.Headers.Pragma = "no-cache";
            return Results.Ok(new TicketDetail(row.Ticket.Id, row.Ticket.OrderId, row.Ticket.SessionId, row.EventTitle, row.VenueName, row.VenueAddress,
                row.City, row.StartsAt, OrganizerCatalogEndpoints.FormatLocal(row.StartsAt, row.Timezone), row.Timezone, row.Ticket.Label, row.Ticket.Status,
                row.Ticket.IssuedAt, row.Ticket.CheckedInAt, CheckInWindow.OpensAt(row.StartsAt, policy.CheckInOpensBefore),
                CheckInWindow.ClosesAt(row.StartsAt, policy.CheckInClosesAfter), token, await clock.NowAsync(ct), row.IsDemo));
        });

        var gate = app.MapGroup("/api/gate").RequireAuthorization().WithTags("Gate");

        gate.MapGet("/sessions", async (HttpContext http, AppDbContext db, ICurrentUser user, IClock clock, IOptions<PolicyOptions> policies, CancellationToken ct) =>
        {
            var userId = user.RequireUserId();
            var now = await clock.NowAsync(ct);
            var policy = policies.Value;
            var rows = await (from a in db.GateAssignments.AsNoTracking()
                              join s in db.Sessions on a.SessionId equals s.Id
                              where a.UserId == userId && db.OrganizationMembers.Any(m => m.OrganizationId == s.OrganizationId && m.UserId == userId)
                              orderby s.StartsAt, s.Id
                              select new
                              {
                                  s.Id, EventTitle = s.Event.Title, VenueName = s.Venue.Name, s.City, s.StartsAt, s.Timezone, s.Status,
                                  Total = db.Tickets.Count(t => t.SessionId == s.Id && t.Status != TicketStatus.Cancelled),
                                  CheckedIn = db.Tickets.Count(t => t.SessionId == s.Id && t.Status == TicketStatus.CheckedIn),
                              }).ToListAsync(ct);
            http.Response.Headers.CacheControl = "no-store";
            // Kapı yanıtı sayım ve seans bilgisinden ibarettir: müşteri adı, e-posta veya finans verisi içermez.
            return Results.Ok(rows.Select(r => new GateSessionDto(r.Id, r.EventTitle, r.VenueName, r.City, r.StartsAt,
                OrganizerCatalogEndpoints.FormatLocal(r.StartsAt, r.Timezone), r.Timezone, r.Status,
                CheckInWindow.OpensAt(r.StartsAt, policy.CheckInOpensBefore), CheckInWindow.ClosesAt(r.StartsAt, policy.CheckInClosesAfter),
                r.Status == SessionStatus.Published && CheckInWindow.IsOpen(now, r.StartsAt, policy.CheckInOpensBefore, policy.CheckInClosesAfter),
                r.Total, r.CheckedIn, now)));
        });

        gate.MapPost("/sessions/{sessionId:guid}/check-ins", async (Guid sessionId, HttpContext http, IIdempotentEndpoint idempotent, CheckInService checkIns,
                GateAccess access, AppDbContext db, ICurrentUser user, IClock clock, AppMetrics metrics) =>
            {
                try
                {
                    // Yetki, kayıtlı yanıt oynatılmadan önce doğrulanır: ataması kaldırılan personel eski "Kabul" yanıtını alamaz.
                    var result = await idempotent.RunAsync<CheckInRequest>(http, "ticket.checkin",
                        (request, ct) => checkIns.CheckInAsync(sessionId, request, ct),
                        authorize: ct => access.RequireAssignedAsync(sessionId, ct));
                    metrics.Increment("loca_checkins_total", ("result", "accepted"));
                    return result;
                }
                catch (ApiException e) when (e.Extensions?.ContainsKey("result") == true)
                {
                    metrics.Increment("loca_checkins_total", ("result", e.Code));
                    // Reddedilen tarama, geri alınan transaction'ın dışında kaydedilir (token içermez).
                    var ticketId = e.Extensions.TryGetValue("ticketId", out var id) && id is Guid guid ? guid : (Guid?)null;
                    db.ChangeTracker.Clear();
                    db.ScanLog.Add(new ScanLogEntry
                    {
                        SessionId = sessionId, TicketId = ticketId, StaffUserId = user.RequireUserId(), Result = e.Code, CreatedAt = await clock.NowAsync(http.RequestAborted),
                    });
                    await db.SaveChangesAsync(http.RequestAborted);
                    throw;
                }
            })
            .RequireRateLimiting(RateLimitPolicies.CheckIn);

        var organizer = app.MapGroup("/api/organizer/sessions/{sessionId:guid}/gate-staff").RequireAuthorization().WithTags("Organizer");

        organizer.MapGet("/", async (Guid sessionId, AppDbContext db, CatalogAccess access, CancellationToken ct) =>
        {
            var organizationId = await access.RequireSessionAdminAsync(sessionId, ct);
            var staff = await db.OrganizationMembers.AsNoTracking().Where(m => m.OrganizationId == organizationId)
                .OrderBy(m => m.User.DisplayName).ThenBy(m => m.UserId)
                .Select(m => new GateStaffDto(m.UserId, m.User.DisplayName, db.GateAssignments.Any(a => a.SessionId == sessionId && a.UserId == m.UserId)))
                .ToListAsync(ct);
            return Results.Ok(staff);
        });

        organizer.MapPut("/{userId:guid}", async (Guid sessionId, Guid userId, AppDbContext db, CatalogAccess access, ICurrentUser user, IClock clock,
            IAuditWriter audit, CancellationToken ct) =>
        {
            var organizationId = await access.RequireSessionAdminAsync(sessionId, ct);
            // Yalnız kendi organizasyonunun üyesi atanabilir; başka organizasyonun kullanıcısı "yok" sayılır.
            if (!await db.OrganizationMembers.AnyAsync(m => m.OrganizationId == organizationId && m.UserId == userId, ct))
                throw ApiErrors.Unprocessable(ErrorCodes.UserNotFound, "This user is not a member of the organization.");
            if (!await db.GateAssignments.AnyAsync(a => a.SessionId == sessionId && a.UserId == userId, ct))
            {
                var now = await clock.NowAsync(ct);
                db.GateAssignments.Add(new GateAssignment { SessionId = sessionId, UserId = userId, AssignedBy = user.RequireUserId(), CreatedAt = now });
                audit.Add(now, "gate.assigned", "session", sessionId, organizationId, new { staffUserId = userId });
                await db.SaveChangesAsync(ct);
            }
            return Results.NoContent();
        });

        organizer.MapDelete("/{userId:guid}", async (Guid sessionId, Guid userId, AppDbContext db, CatalogAccess access, IClock clock, IAuditWriter audit, CancellationToken ct) =>
        {
            var organizationId = await access.RequireSessionAdminAsync(sessionId, ct);
            var removed = await db.GateAssignments.Where(a => a.SessionId == sessionId && a.UserId == userId).ExecuteDeleteAsync(ct);
            if (removed > 0)
            {
                audit.Add(await clock.NowAsync(ct), "gate.unassigned", "session", sessionId, organizationId, new { staffUserId = userId });
                await db.SaveChangesAsync(ct);
            }
            return Results.NoContent();
        });
    }
}
