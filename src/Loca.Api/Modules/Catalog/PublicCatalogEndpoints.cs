using System.Globalization;
using System.Text;
using Loca.Api.Infrastructure.Auth;
using Loca.Api.Infrastructure.Http;
using Loca.Api.Infrastructure.Persistence;
using Loca.Api.Infrastructure.RateLimiting;
using Loca.Api.Infrastructure.Time;
using Loca.Api.Modules.Identity;
using Loca.Api.Modules.Operations;
using Loca.Domain;
using Microsoft.EntityFrameworkCore;

namespace Loca.Api.Modules.Catalog;

public sealed record PublicSessionListItem(Guid SessionId, string EventSlug, string EventTitle, EventCategory Category, string City,
    string VenueName, DateTimeOffset StartsAt, string StartsAtLocal, string Timezone, SalesMode SalesMode, long? MinPriceMinor,
    string Currency, SalesWindowState SalesState, DateTimeOffset SalesStartAt, bool SoldOut, bool IsDemo);
public sealed record PublicSessionPage(IReadOnlyList<PublicSessionListItem> Items, string? NextCursor, DateTimeOffset ServerTime);
public sealed record PublicFilters(IReadOnlyList<string> Cities, IReadOnlyList<EventCategory> Categories);
public sealed record PublicCategoryDto(Guid Id, string Name, long PriceMinor, int Available);
public sealed record PublicSessionDetail(Guid SessionId, string EventSlug, string EventTitle, string EventDescription, EventCategory Category,
    string OrganizerName, string VenueName, string VenueAddress, string City, DateTimeOffset StartsAt, string StartsAtLocal, string Timezone,
    SalesMode SalesMode, DateTimeOffset SalesStartAt, DateTimeOffset SalesEndAt, SalesWindowState SalesState, int HoldTtlSeconds,
    int MaxTicketsPerHold, string Currency, bool DemoSimulatorEnabled, bool IsDemo, SeatCountsPublic? Seats,
    IReadOnlyList<PublicTierDto> Tiers, IReadOnlyList<PublicCategoryDto> Categories, DateTimeOffset ServerTime);
public sealed record SeatCountsPublic(int Total, int Available);
public sealed record PublicTierDto(string TierCode, long PriceMinor);
public sealed record PublicSeatDto(Guid Id, string Section, string Row, int Number, int X, int Y, string TierCode, long PriceMinor, bool Available);
public sealed record PublicSeatMap(Guid SessionId, DateTimeOffset ServerTime, IReadOnlyList<PublicSeatDto> Seats);
public sealed record PublicEventDetail(string Slug, string Title, string Description, EventCategory Category, string OrganizerName, bool IsDemo,
    IReadOnlyList<PublicSessionListItem> Sessions, DateTimeOffset ServerTime);

internal static class PublicCatalogEndpoints
{
    private const int DefaultLimit = 20;
    private const int MaxLimit = 50;

    public static void Map(IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/catalog").AllowAnonymous().RequireRateLimiting(RateLimitPolicies.Catalog).WithTags("Catalog");
        api.MapGet("/sessions", ListAsync);
        api.MapGet("/filters", FiltersAsync);
        api.MapGet("/events/{slug}", EventAsync);
        api.MapGet("/sessions/{sessionId:guid}", DetailAsync);
        api.MapGet("/sessions/{sessionId:guid}/seats", SeatsAsync);

        var admin = app.MapGroup("/api/admin/events").RequireAuthorization().WithTags("Admin");
        admin.MapPost("/{eventId:guid}/takedown", (Guid eventId, AppDbContext db, ICurrentUser user, IClock clock, IAuditWriter audit, CancellationToken ct) =>
            ModerateAsync(eventId, blocked: true, db, user, clock, audit, ct));
        admin.MapPost("/{eventId:guid}/clear-takedown", (Guid eventId, AppDbContext db, ICurrentUser user, IClock clock, IAuditWriter audit, CancellationToken ct) =>
            ModerateAsync(eventId, blocked: false, db, user, clock, audit, ct));
    }

    /// <summary>
    /// Public görünürlük kuralı tek yerde: yayınlanmış seans + yayınlanmış ve kaldırılmamış etkinlik + aktif organizasyon + başlamamış.
    /// </summary>
    internal static IQueryable<Session> PublicSessions(AppDbContext db, DateTimeOffset now) =>
        db.Sessions.AsNoTracking().Where(s =>
            s.Status == SessionStatus.Published &&
            s.StartsAt > now &&
            s.Event.Status == EventStatus.Published &&
            s.Event.ModerationBlockedAt == null &&
            s.Event.Organization.Status == OrganizationStatus.Active);

    private static async Task<IResult> ListAsync(HttpContext http, AppDbContext db, IClock clock,
        string? city, EventCategory? category, DateTimeOffset? from, DateTimeOffset? to, string? cursor, int? limit, CancellationToken ct)
    {
        var take = limit ?? DefaultLimit;
        new Validator()
            .Check(take is >= 1 and <= MaxLimit, "limit", $"Limit must be between 1 and {MaxLimit}.")
            .Check(city is null || city.Length <= 80, "city", "City is too long.")
            .Check(from is null || to is null || from < to, "to", "'to' must be later than 'from'.")
            .ThrowIfInvalid();

        var now = await clock.NowAsync(ct);
        var query = PublicSessions(db, now);
        if (!string.IsNullOrWhiteSpace(city)) query = query.Where(s => s.City == city);
        if (category is not null) query = query.Where(s => s.Event.Category == category);
        if (from is not null) query = query.Where(s => s.StartsAt >= from.Value.ToUniversalTime());
        if (to is not null) query = query.Where(s => s.StartsAt < to.Value.ToUniversalTime());

        if (cursor is not null)
        {
            var (afterStart, afterId) = DecodeCursor(cursor);
            query = query.Where(s => s.StartsAt > afterStart || (s.StartsAt == afterStart && s.Id > afterId));
        }

        // Deterministik sıra: (starts_at, id). Bir sonraki sayfa imleci son satırdan üretilir.
        var page = await Project(db, query.OrderBy(s => s.StartsAt).ThenBy(s => s.Id).Take(take + 1), now).ToListAsync(ct);
        var hasMore = page.Count > take;
        if (hasMore) page.RemoveAt(page.Count - 1);
        var items = page.Select(r => r.ToItem(now)).ToList();
        var next = hasMore ? EncodeCursor(items[^1].StartsAt, items[^1].SessionId) : null;

        http.Response.Headers.CacheControl = "public, max-age=30";
        return Results.Ok(new PublicSessionPage(items, next, now));
    }

    private static async Task<IResult> FiltersAsync(HttpContext http, AppDbContext db, IClock clock, CancellationToken ct)
    {
        var now = await clock.NowAsync(ct);
        var cities = await PublicSessions(db, now).Select(s => s.City).Distinct().OrderBy(c => c).ToListAsync(ct);
        var categories = await PublicSessions(db, now).Select(s => s.Event.Category).Distinct().ToListAsync(ct);
        http.Response.Headers.CacheControl = "public, max-age=30";
        return Results.Ok(new PublicFilters(cities, [.. categories.Order()]));
    }

    private static async Task<IResult> EventAsync(string slug, HttpContext http, AppDbContext db, IClock clock, CancellationToken ct)
    {
        var now = await clock.NowAsync(ct);
        var found = await db.Events.AsNoTracking()
            .Where(e => e.Slug == slug && e.Status == EventStatus.Published && e.ModerationBlockedAt == null && e.Organization.Status == OrganizationStatus.Active)
            .Select(e => new { e.Id, e.Slug, e.Title, e.Description, e.Category, OrganizerName = e.Organization.Name, e.IsDemo })
            .FirstOrDefaultAsync(ct) ?? throw ApiErrors.NotFound();

        var sessions = await Project(db, PublicSessions(db, now).Where(s => s.EventId == found.Id).OrderBy(s => s.StartsAt).ThenBy(s => s.Id), now).ToListAsync(ct);
        http.Response.Headers.CacheControl = "public, max-age=30";
        return Results.Ok(new PublicEventDetail(found.Slug, found.Title, found.Description, found.Category, found.OrganizerName, found.IsDemo,
            [.. sessions.Select(s => s.ToItem(now))], now));
    }

    private static async Task<IResult> DetailAsync(Guid sessionId, HttpContext http, AppDbContext db, IClock clock, CancellationToken ct)
    {
        var now = await clock.NowAsync(ct);
        var session = await PublicSessions(db, now).Where(s => s.Id == sessionId)
            .Select(s => new
            {
                s.Id, EventSlug = s.Event.Slug, EventTitle = s.Event.Title, EventDescription = s.Event.Description, s.Event.Category,
                OrganizerName = s.Event.Organization.Name, VenueName = s.Venue.Name, VenueAddress = s.Venue.Address, s.City, s.StartsAt,
                s.Timezone, s.SalesMode, s.SalesStartAt, s.SalesEndAt, s.HoldTtlSeconds, s.DemoSimulatorEnabled, s.Event.IsDemo,
            })
            .FirstOrDefaultAsync(ct) ?? throw ApiErrors.NotFound();

        SeatCountsPublic? seats = null;
        IReadOnlyList<PublicTierDto> tiers = [];
        IReadOnlyList<PublicCategoryDto> categories = [];
        if (session.SalesMode == SalesMode.ReservedSeating)
        {
            var rows = await db.SessionSeats.AsNoTracking().Where(s => s.SessionId == sessionId)
                .GroupBy(s => new { s.TierCode, s.PriceMinor })
                .Select(g => new
                {
                    g.Key.TierCode, g.Key.PriceMinor, Total = g.Count(),
                    Available = g.Count(s => s.Status == SeatStatus.Available || (s.Status == SeatStatus.Held && s.HeldUntil <= now)),
                }).ToListAsync(ct);
            seats = new SeatCountsPublic(rows.Sum(r => r.Total), rows.Sum(r => r.Available));
            tiers = [.. rows.GroupBy(r => r.TierCode).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => new PublicTierDto(g.Key, g.Min(r => r.PriceMinor)))];
        }
        else
        {
            categories = await db.GaCategories.AsNoTracking().Where(c => c.SessionId == sessionId).OrderBy(c => c.SortOrder).ThenBy(c => c.Id)
                .Select(c => new PublicCategoryDto(c.Id, c.Name, c.PriceMinor, c.Capacity - c.HeldCount - c.SoldCount)).ToListAsync(ct);
        }

        http.Response.Headers.CacheControl = "no-store";
        return Results.Ok(new PublicSessionDetail(session.Id, session.EventSlug, session.EventTitle, session.EventDescription, session.Category,
            session.OrganizerName, session.VenueName, session.VenueAddress, session.City, session.StartsAt,
            OrganizerCatalogEndpoints.FormatLocal(session.StartsAt, session.Timezone), session.Timezone, session.SalesMode, session.SalesStartAt,
            session.SalesEndAt, SalesWindow.Evaluate(now, session.SalesStartAt, session.SalesEndAt), session.HoldTtlSeconds,
            HoldPolicy.MaxTicketsPerHold, Money.SupportedCurrency, session.DemoSimulatorEnabled, session.IsDemo, seats, tiers, categories, now));
    }

    private static async Task<IResult> SeatsAsync(Guid sessionId, HttpContext http, AppDbContext db, IClock clock, CancellationToken ct)
    {
        var now = await clock.NowAsync(ct);
        if (!await PublicSessions(db, now).AnyAsync(s => s.Id == sessionId && s.SalesMode == SalesMode.ReservedSeating, ct))
            throw ApiErrors.NotFound();

        // Müsaitlik yalnız ipucudur: tutulmuş ile satılmış ayrımı ve sahiplik bilgisi verilmez.
        var seats = await db.SessionSeats.AsNoTracking().Where(s => s.SessionId == sessionId)
            .OrderBy(s => s.Y).ThenBy(s => s.X).ThenBy(s => s.Id)
            .Select(s => new PublicSeatDto(s.Id, s.Section, s.RowLabel, s.SeatNumber, s.X, s.Y, s.TierCode, s.PriceMinor,
                s.Status == SeatStatus.Available || (s.Status == SeatStatus.Held && s.HeldUntil <= now)))
            .ToListAsync(ct);
        http.Response.Headers.CacheControl = "no-store";
        return Results.Ok(new PublicSeatMap(sessionId, now, seats));
    }

    private static async Task<IResult> ModerateAsync(Guid eventId, bool blocked, AppDbContext db, ICurrentUser user, IClock clock, IAuditWriter audit, CancellationToken ct)
    {
        user.RequirePlatformAdmin();
        var found = await db.Events.FirstOrDefaultAsync(e => e.Id == eventId, ct) ?? throw ApiErrors.NotFound();
        var now = await clock.NowAsync(ct);
        found.ModerationBlockedAt = blocked ? found.ModerationBlockedAt ?? now : null;
        found.UpdatedAt = now;
        audit.Add(now, blocked ? "event.taken_down" : "event.takedown_cleared", "event", eventId, found.OrganizationId);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { id = eventId, moderationBlocked = blocked });
    }

    private sealed record Row(Guid SessionId, string EventSlug, string EventTitle, EventCategory Category, string City, string VenueName,
        DateTimeOffset StartsAt, string Timezone, SalesMode SalesMode, DateTimeOffset SalesStartAt, DateTimeOffset SalesEndAt, bool IsDemo,
        long? MinSeatPrice, long? MinCategoryPrice, bool AnySeatAvailable, bool AnyCapacityAvailable)
    {
        public PublicSessionListItem ToItem(DateTimeOffset now) => new(SessionId, EventSlug, EventTitle, Category, City, VenueName, StartsAt,
            OrganizerCatalogEndpoints.FormatLocal(StartsAt, Timezone), Timezone, SalesMode,
            SalesMode == SalesMode.ReservedSeating ? MinSeatPrice : MinCategoryPrice, Money.SupportedCurrency,
            SalesWindow.Evaluate(now, SalesStartAt, SalesEndAt), SalesStartAt,
            SoldOut: SalesMode == SalesMode.ReservedSeating ? !AnySeatAvailable : !AnyCapacityAvailable, IsDemo);
    }

    private static IQueryable<Row> Project(AppDbContext db, IQueryable<Session> sessions, DateTimeOffset now) =>
        sessions.Select(s => new Row(s.Id, s.Event.Slug, s.Event.Title, s.Event.Category, s.City, s.Venue.Name, s.StartsAt, s.Timezone,
            s.SalesMode, s.SalesStartAt, s.SalesEndAt, s.Event.IsDemo,
            db.SessionSeats.Where(x => x.SessionId == s.Id).Min(x => (long?)x.PriceMinor),
            db.GaCategories.Where(x => x.SessionId == s.Id).Min(x => (long?)x.PriceMinor),
            db.SessionSeats.Any(x => x.SessionId == s.Id && (x.Status == SeatStatus.Available || (x.Status == SeatStatus.Held && x.HeldUntil <= now))),
            db.GaCategories.Any(x => x.SessionId == s.Id && x.HeldCount + x.SoldCount < x.Capacity)));

    internal static string EncodeCursor(DateTimeOffset startsAt, Guid id) =>
        Convert.ToBase64String(Encoding.ASCII.GetBytes($"{startsAt.UtcTicks.ToString(CultureInfo.InvariantCulture)}:{id:N}"))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal static (DateTimeOffset StartsAt, Guid Id) DecodeCursor(string cursor)
    {
        try
        {
            if (cursor.Length > 120) throw new FormatException();
            var padded = cursor.Replace('-', '+').Replace('_', '/');
            padded += new string('=', (4 - padded.Length % 4) % 4);
            var parts = Encoding.ASCII.GetString(Convert.FromBase64String(padded)).Split(':');
            if (parts.Length != 2) throw new FormatException();
            var ticks = long.Parse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture);
            return (new DateTimeOffset(ticks, TimeSpan.Zero), Guid.ParseExact(parts[1], "N"));
        }
        catch (Exception e) when (e is FormatException or OverflowException or ArgumentException)
        {
            throw ApiErrors.BadRequest(ErrorCodes.ValidationFailed, "The cursor is not valid.");
        }
    }
}
