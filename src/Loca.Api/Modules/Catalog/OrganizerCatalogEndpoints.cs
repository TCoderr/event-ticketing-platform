using System.Globalization;
using Loca.Api.Infrastructure.Auth;
using Loca.Api.Infrastructure.Config;
using Loca.Api.Infrastructure.Http;
using Loca.Api.Infrastructure.Persistence;
using Loca.Api.Infrastructure.Time;
using Loca.Api.Infrastructure.Tx;
using Loca.Api.Modules.Identity;
using Loca.Api.Modules.Inventory;
using Loca.Api.Modules.Operations;
using Loca.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Loca.Api.Modules.Catalog;

public sealed record VenueRequest(string Name, string City, string Address, string Timezone);
public sealed record VenueDto(Guid Id, Guid OrganizationId, string Name, string City, string Address, string Timezone);

public sealed record LayoutSectionRequest(string Name, string TierCode, int Rows, int SeatsPerRow, int? AisleAfter);
public sealed record CreateLayoutRequest(string Name, IReadOnlyList<LayoutSectionRequest> Sections);
public sealed record LayoutDto(Guid Id, Guid VenueId, int Version, string Name, int SeatCount, DateTimeOffset CreatedAt, IReadOnlyList<string> TierCodes);
public sealed record LayoutSeatDto(Guid Id, string Section, string Row, int Number, int X, int Y, string TierCode);

public sealed record EventRequest(string Title, string Description, EventCategory Category);
public sealed record OrganizerEventDto(Guid Id, Guid OrganizationId, string Title, string Slug, string Description,
    EventCategory Category, EventStatus Status, bool ModerationBlocked, bool IsDemo, IReadOnlyList<OrganizerSessionSummaryDto> Sessions);
public sealed record OrganizerSessionSummaryDto(Guid Id, DateTimeOffset StartsAt, string StartsAtLocal, string Timezone, string VenueName,
    string City, SalesMode SalesMode, SessionStatus Status);

public sealed record CreateSessionRequest(Guid VenueId, string StartsAtLocal, string? SalesStartAtLocal, string? SalesEndAtLocal,
    SalesMode SalesMode, int? HoldTtlSeconds);
public sealed record UpdateSessionRequest(Guid VenueId, string StartsAtLocal, string? SalesStartAtLocal, string? SalesEndAtLocal, int? HoldTtlSeconds);
public sealed record SeatingRequest(Guid LayoutId, Dictionary<string, long> TierPrices, string? Currency);
public sealed record TierPricesRequest(Dictionary<string, long> TierPrices, string? Currency);
public sealed record CategoryRequest(string Name, long PriceMinor, int Capacity, string? Currency);
public sealed record DemoSimulatorRequest(bool Enabled);

public sealed record TierDto(string TierCode, long PriceMinor, int SeatCount);
public sealed record SeatCountsDto(int Total, int Available, int Held, int Sold);
public sealed record OrganizerCategoryDto(Guid Id, string Name, long PriceMinor, int Capacity, int Held, int Sold, int Available);
public sealed record OrganizerSessionDto(Guid Id, Guid EventId, string EventTitle, Guid VenueId, string VenueName, string City, string Timezone,
    DateTimeOffset StartsAt, string StartsAtLocal, DateTimeOffset SalesStartAt, DateTimeOffset SalesEndAt, SalesMode SalesMode,
    SessionStatus Status, int HoldTtlSeconds, Guid? LayoutId, bool DemoSimulatorEnabled, string Currency,
    SeatCountsDto? Seats, IReadOnlyList<TierDto> Tiers, IReadOnlyList<OrganizerCategoryDto> Categories);

/// <summary>Kaynağın organizasyonunu veritabanından bulur ve çağıranın o organizasyonda yönetici olduğunu doğrular.</summary>
internal sealed class CatalogAccess(AppDbContext db, IOrganizationAccess access)
{
    public async Task<Guid> RequireVenueAdminAsync(Guid venueId, CancellationToken ct) =>
        await RequireAsync(db.Venues.Where(v => v.Id == venueId).Select(v => (Guid?)v.OrganizationId), ct);

    public async Task<Guid> RequireEventAdminAsync(Guid eventId, CancellationToken ct) =>
        await RequireAsync(db.Events.Where(e => e.Id == eventId).Select(e => (Guid?)e.OrganizationId), ct);

    public async Task<Guid> RequireSessionAdminAsync(Guid sessionId, CancellationToken ct) =>
        await RequireAsync(db.Sessions.Where(s => s.Id == sessionId).Select(s => (Guid?)s.OrganizationId), ct);

    private async Task<Guid> RequireAsync(IQueryable<Guid?> organizationQuery, CancellationToken ct)
    {
        var organizationId = await organizationQuery.FirstOrDefaultAsync(ct) ?? throw ApiErrors.NotFound();
        await access.RequireAdminAsync(organizationId, ct);
        return organizationId;
    }
}

internal static class OrganizerCatalogEndpoints
{
    private const int MaxSeatsPerLayout = 2000;
    private const int MaxCategoriesPerSession = 10;

    public static void Map(IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/organizer").RequireAuthorization().WithTags("Organizer");

        api.MapGet("/organizations/{organizationId:guid}/venues", ListVenuesAsync);
        api.MapPost("/organizations/{organizationId:guid}/venues", CreateVenueAsync);
        api.MapGet("/venues/{venueId:guid}/layouts", ListLayoutsAsync);
        api.MapPost("/venues/{venueId:guid}/layouts", CreateLayoutAsync);
        api.MapGet("/layouts/{layoutId:guid}/seats", GetLayoutSeatsAsync);

        api.MapGet("/organizations/{organizationId:guid}/events", ListEventsAsync);
        api.MapPost("/organizations/{organizationId:guid}/events", CreateEventAsync);
        api.MapGet("/events/{eventId:guid}", GetEventAsync);
        api.MapPut("/events/{eventId:guid}", UpdateEventAsync);
        api.MapPost("/events/{eventId:guid}/publish", PublishEventAsync);
        api.MapPost("/events/{eventId:guid}/unpublish", UnpublishEventAsync);

        api.MapPost("/events/{eventId:guid}/sessions", CreateSessionAsync);
        api.MapGet("/sessions/{sessionId:guid}", GetSessionAsync);
        api.MapPut("/sessions/{sessionId:guid}", UpdateSessionAsync);
        api.MapPut("/sessions/{sessionId:guid}/seating", SetSeatingAsync);
        api.MapPut("/sessions/{sessionId:guid}/tier-prices", SetTierPricesAsync);
        api.MapPost("/sessions/{sessionId:guid}/categories", CreateCategoryAsync);
        api.MapPut("/sessions/{sessionId:guid}/categories/{categoryId:guid}", UpdateCategoryAsync);
        api.MapPost("/sessions/{sessionId:guid}/publish", PublishSessionAsync);
        api.MapPut("/sessions/{sessionId:guid}/demo-simulator", SetDemoSimulatorAsync);
    }

    // --- Mekan ve plan ---------------------------------------------------------------------------

    private static async Task<IResult> ListVenuesAsync(Guid organizationId, AppDbContext db, IOrganizationAccess access, CancellationToken ct)
    {
        await access.RequireAdminAsync(organizationId, ct);
        var venues = await db.Venues.AsNoTracking().Where(v => v.OrganizationId == organizationId)
            .OrderBy(v => v.Name).ThenBy(v => v.Id)
            .Select(v => new VenueDto(v.Id, v.OrganizationId, v.Name, v.City, v.Address, v.Timezone)).ToListAsync(ct);
        return Results.Ok(venues);
    }

    private static async Task<IResult> CreateVenueAsync(Guid organizationId, VenueRequest request, AppDbContext db,
        IOrganizationAccess access, IClock clock, IAuditWriter audit, CancellationToken ct)
    {
        await access.RequireAdminAsync(organizationId, ct);
        new Validator()
            .Required(request.Name, "name", 160, 2)
            .Required(request.City, "city", 80, 2)
            .Required(request.Address, "address", 300, 2)
            .ThrowIfInvalid();
        if (!LocalTimeResolver.TryFindTimeZone(request.Timezone, out _))
            throw ApiErrors.BadRequest(ErrorCodes.InvalidTimezone, "Timezone must be a valid IANA identifier, for example Europe/Istanbul.");

        var now = await clock.NowAsync(ct);
        var venue = new Venue
        {
            Id = Guid.CreateVersion7(), OrganizationId = organizationId, Name = request.Name.Trim(), City = request.City.Trim(),
            Address = request.Address.Trim(), Timezone = request.Timezone, CreatedAt = now,
        };
        db.Venues.Add(venue);
        audit.Add(now, "venue.created", "venue", venue.Id, organizationId);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/organizer/venues/{venue.Id}/layouts",
            new VenueDto(venue.Id, venue.OrganizationId, venue.Name, venue.City, venue.Address, venue.Timezone));
    }

    private static async Task<IResult> ListLayoutsAsync(Guid venueId, AppDbContext db, CatalogAccess access, CancellationToken ct)
    {
        await access.RequireVenueAdminAsync(venueId, ct);
        var layouts = await db.SeatLayouts.AsNoTracking().Where(l => l.VenueId == venueId).OrderByDescending(l => l.Version).ToListAsync(ct);
        var ids = layouts.Select(l => l.Id).ToList();
        var tiers = await db.LayoutSeats.AsNoTracking().Where(s => ids.Contains(s.LayoutId))
            .Select(s => new { s.LayoutId, s.TierCode }).Distinct().ToListAsync(ct);
        return Results.Ok(layouts.Select(l => new LayoutDto(l.Id, l.VenueId, l.Version, l.Name, l.SeatCount, l.CreatedAt,
            [.. tiers.Where(t => t.LayoutId == l.Id).Select(t => t.TierCode).Order(StringComparer.Ordinal)])));
    }

    private static async Task<IResult> CreateLayoutAsync(Guid venueId, CreateLayoutRequest request, AppDbContext db, CatalogAccess access,
        ITransactionRunner transactions, IClock clock, IAuditWriter audit, CancellationToken ct)
    {
        var organizationId = await access.RequireVenueAdminAsync(venueId, ct);
        var sections = request.Sections ?? [];
        var validator = new Validator()
            .Required(request.Name, "name", 120, 2)
            .Check(sections.Count is >= 1 and <= 10, "sections", "Between 1 and 10 sections are required.");
        for (var i = 0; i < sections.Count; i++)
        {
            var section = sections[i];
            validator
                .Required(section.Name, $"sections[{i}].name", 40)
                .Check(section.TierCode is { Length: >= 1 and <= 8 } && section.TierCode.All(char.IsAsciiLetterOrDigit), $"sections[{i}].tierCode", "Tier code must be 1-8 ASCII letters or digits.")
                .Check(section.Rows is >= 1 and <= 40, $"sections[{i}].rows", "Rows must be between 1 and 40.")
                .Check(section.SeatsPerRow is >= 1 and <= 60, $"sections[{i}].seatsPerRow", "Seats per row must be between 1 and 60.")
                .Check(section.AisleAfter is null || (section.AisleAfter >= 1 && section.AisleAfter < section.SeatsPerRow), $"sections[{i}].aisleAfter", "Aisle must fall between two seats of the row.");
        }
        validator
            .Check(sections.Select(s => s.Name?.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() == sections.Count, "sections", "Section names must be unique.")
            .Check(sections.Sum(s => (long)Math.Max(0, s.Rows) * Math.Max(0, s.SeatsPerRow)) <= MaxSeatsPerLayout, "sections", $"A layout can contain at most {MaxSeatsPerLayout} seats.")
            .ThrowIfInvalid();

        var layout = await transactions.RunAsync(async token =>
        {
            // Sürüm numarası mekan satırı kilidi altında atanır; eşzamanlı iki oluşturma aynı sürümü alamaz.
            await db.Venues.FromSql($"SELECT * FROM catalog.venues WHERE id = {venueId} FOR UPDATE").AsNoTracking().SingleAsync(token);
            var now = await clock.NowAsync(token);
            var version = (await db.SeatLayouts.Where(l => l.VenueId == venueId).MaxAsync(l => (int?)l.Version, token) ?? 0) + 1;
            var seats = LayoutGenerator.Generate(sections);
            var created = new SeatLayout { Id = Guid.CreateVersion7(), VenueId = venueId, Version = version, Name = request.Name.Trim(), SeatCount = seats.Count, CreatedAt = now };
            foreach (var seat in seats) seat.LayoutId = created.Id;
            db.SeatLayouts.Add(created);
            db.LayoutSeats.AddRange(seats);
            audit.Add(now, "layout.created", "seat_layout", created.Id, organizationId, new { venueId, version, seatCount = seats.Count });
            await db.SaveChangesAsync(token);
            return created;
        }, ct);

        return Results.Created($"/api/organizer/layouts/{layout.Id}/seats",
            new LayoutDto(layout.Id, layout.VenueId, layout.Version, layout.Name, layout.SeatCount, layout.CreatedAt,
                [.. sections.Select(s => s.TierCode).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)]));
    }

    private static async Task<IResult> GetLayoutSeatsAsync(Guid layoutId, AppDbContext db, CatalogAccess access, CancellationToken ct)
    {
        var venueId = await db.SeatLayouts.AsNoTracking().Where(l => l.Id == layoutId).Select(l => (Guid?)l.VenueId).FirstOrDefaultAsync(ct)
            ?? throw ApiErrors.NotFound();
        await access.RequireVenueAdminAsync(venueId, ct);
        var seats = await db.LayoutSeats.AsNoTracking().Where(s => s.LayoutId == layoutId)
            .OrderBy(s => s.Y).ThenBy(s => s.X)
            .Select(s => new LayoutSeatDto(s.Id, s.Section, s.RowLabel, s.SeatNumber, s.X, s.Y, s.TierCode)).ToListAsync(ct);
        return Results.Ok(seats);
    }

    // --- Etkinlik --------------------------------------------------------------------------------

    private static async Task<IResult> ListEventsAsync(Guid organizationId, AppDbContext db, IOrganizationAccess access, CancellationToken ct)
    {
        await access.RequireAdminAsync(organizationId, ct);
        var events = await db.Events.AsNoTracking().Where(e => e.OrganizationId == organizationId)
            .OrderByDescending(e => e.CreatedAt).ThenBy(e => e.Id)
            .Include(e => e.Sessions).ThenInclude(s => s.Venue)
            .AsSplitQuery()
            .ToListAsync(ct);
        return Results.Ok(events.Select(ToDto));
    }

    private static async Task<IResult> CreateEventAsync(Guid organizationId, EventRequest request, AppDbContext db,
        IOrganizationAccess access, IClock clock, IAuditWriter audit, CancellationToken ct)
    {
        await access.RequireAdminAsync(organizationId, ct);
        ValidateEvent(request);
        var now = await clock.NowAsync(ct);
        var created = new Event
        {
            Id = Guid.CreateVersion7(), OrganizationId = organizationId, Title = request.Title.Trim(), Slug = Slugs.Create(request.Title),
            Description = request.Description.Trim(), Category = request.Category, Status = EventStatus.Draft, CreatedAt = now, UpdatedAt = now,
        };
        db.Events.Add(created);
        audit.Add(now, "event.created", "event", created.Id, organizationId);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/organizer/events/{created.Id}", ToDto(created));
    }

    private static async Task<IResult> GetEventAsync(Guid eventId, AppDbContext db, CatalogAccess access, CancellationToken ct)
    {
        await access.RequireEventAdminAsync(eventId, ct);
        var found = await db.Events.AsNoTracking().Include(e => e.Sessions).ThenInclude(s => s.Venue).AsSplitQuery()
            .FirstOrDefaultAsync(e => e.Id == eventId, ct) ?? throw ApiErrors.NotFound();
        return Results.Ok(ToDto(found));
    }

    private static async Task<IResult> UpdateEventAsync(Guid eventId, EventRequest request, AppDbContext db, CatalogAccess access,
        IClock clock, IAuditWriter audit, CancellationToken ct)
    {
        var organizationId = await access.RequireEventAdminAsync(eventId, ct);
        ValidateEvent(request);
        var found = await db.Events.Include(e => e.Sessions).ThenInclude(s => s.Venue).FirstOrDefaultAsync(e => e.Id == eventId, ct) ?? throw ApiErrors.NotFound();
        if (found.Status == EventStatus.Cancelled)
            throw ApiErrors.Conflict(ErrorCodes.InvalidStateTransition, "A cancelled event cannot be edited.");
        var now = await clock.NowAsync(ct);
        found.Title = request.Title.Trim();
        found.Description = request.Description.Trim();
        found.Category = request.Category;
        found.UpdatedAt = now;
        audit.Add(now, "event.updated", "event", found.Id, organizationId);
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToDto(found));
    }

    private static async Task<IResult> PublishEventAsync(Guid eventId, AppDbContext db, CatalogAccess access, IClock clock, IAuditWriter audit, CancellationToken ct)
    {
        var organizationId = await access.RequireEventAdminAsync(eventId, ct);
        var found = await db.Events.Include(e => e.Organization).FirstOrDefaultAsync(e => e.Id == eventId, ct) ?? throw ApiErrors.NotFound();
        if (found.Organization.Status != OrganizationStatus.Active)
            throw ApiErrors.Conflict(ErrorCodes.OrganizationNotActive, "Only an approved, active organization can publish.");
        if (found.ModerationBlockedAt is not null)
            throw ApiErrors.Conflict(ErrorCodes.ModerationBlocked, "This event was taken down by platform moderation.");
        StateMachines.Ensure(StateMachines.CanTransition(found.Status, EventStatus.Published), found.Status, EventStatus.Published);

        var now = await clock.NowAsync(ct);
        found.Status = EventStatus.Published;
        found.UpdatedAt = now;
        audit.Add(now, "event.published", "event", found.Id, organizationId);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { id = found.Id, status = found.Status });
    }

    private static async Task<IResult> UnpublishEventAsync(Guid eventId, AppDbContext db, CatalogAccess access, IClock clock, IAuditWriter audit, CancellationToken ct)
    {
        var organizationId = await access.RequireEventAdminAsync(eventId, ct);
        var found = await db.Events.FirstOrDefaultAsync(e => e.Id == eventId, ct) ?? throw ApiErrors.NotFound();
        StateMachines.Ensure(StateMachines.CanTransition(found.Status, EventStatus.Draft), found.Status, EventStatus.Draft);
        if (await db.Sessions.AnyAsync(s => s.EventId == eventId && s.Status == SessionStatus.Published, ct))
            throw ApiErrors.Conflict(ErrorCodes.PublishGuardFailed, "An event with published sessions cannot return to draft. Cancel the sessions instead.");

        var now = await clock.NowAsync(ct);
        found.Status = EventStatus.Draft;
        found.UpdatedAt = now;
        audit.Add(now, "event.unpublished", "event", found.Id, organizationId);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { id = found.Id, status = found.Status });
    }

    private static void ValidateEvent(EventRequest request) => new Validator()
        .Required(request.Title, "title", 160, 3)
        .Required(request.Description, "description", 4000, 10)
        .Check(Enum.IsDefined(request.Category), "category", "Unknown category.")
        .ThrowIfInvalid();

    // --- Seans -----------------------------------------------------------------------------------

    private static async Task<IResult> CreateSessionAsync(Guid eventId, CreateSessionRequest request, AppDbContext db, CatalogAccess access,
        IClock clock, IOptions<HoldOptions> holds, IAuditWriter audit, CancellationToken ct)
    {
        var organizationId = await access.RequireEventAdminAsync(eventId, ct);
        new Validator().Check(Enum.IsDefined(request.SalesMode), "salesMode", "Sales mode must be ReservedSeating or GeneralAdmission.").ThrowIfInvalid();
        var parent = await db.Events.AsNoTracking().FirstAsync(e => e.Id == eventId, ct);
        if (parent.Status == EventStatus.Cancelled)
            throw ApiErrors.Conflict(ErrorCodes.InvalidStateTransition, "Sessions cannot be added to a cancelled event.");

        var now = await clock.NowAsync(ct);
        var venue = await RequireOwnVenueAsync(db, request.VenueId, organizationId, ct);
        var times = ResolveTimes(request.StartsAtLocal, request.SalesStartAtLocal, request.SalesEndAtLocal, venue.Timezone, now);
        var ttl = ResolveTtl(request.HoldTtlSeconds, holds.Value);

        var session = new Session
        {
            Id = Guid.CreateVersion7(), EventId = eventId, OrganizationId = organizationId, VenueId = venue.Id, City = venue.City,
            Timezone = venue.Timezone, StartsAt = times.StartsAt, SalesStartAt = times.SalesStartAt, SalesEndAt = times.SalesEndAt,
            SalesMode = request.SalesMode, Status = SessionStatus.Draft, HoldTtlSeconds = ttl, CreatedAt = now,
        };
        db.Sessions.Add(session);
        audit.Add(now, "session.created", "session", session.Id, organizationId, new { eventId, mode = request.SalesMode.ToString() });
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/organizer/sessions/{session.Id}", await LoadSessionDtoAsync(db, session.Id, clock, ct));
    }

    private static async Task<IResult> GetSessionAsync(Guid sessionId, AppDbContext db, CatalogAccess access, IClock clock, CancellationToken ct)
    {
        await access.RequireSessionAdminAsync(sessionId, ct);
        return Results.Ok(await LoadSessionDtoAsync(db, sessionId, clock, ct));
    }

    private static async Task<IResult> UpdateSessionAsync(Guid sessionId, UpdateSessionRequest request, AppDbContext db, CatalogAccess access,
        IClock clock, IOptions<HoldOptions> holds, IAuditWriter audit, CancellationToken ct)
    {
        var organizationId = await access.RequireSessionAdminAsync(sessionId, ct);
        var session = await db.Sessions.FirstAsync(s => s.Id == sessionId, ct);
        if (session.Status != SessionStatus.Draft)
            throw ApiErrors.Conflict(ErrorCodes.SessionNotEditable, "Only draft sessions can be edited.");

        var now = await clock.NowAsync(ct);
        var venue = await RequireOwnVenueAsync(db, request.VenueId, organizationId, ct);
        if (session.LayoutId is not null && venue.Id != session.VenueId)
            throw ApiErrors.Conflict(ErrorCodes.LayoutLocked, "The seating plan belongs to the current venue. Replace the seating before changing the venue.");
        var times = ResolveTimes(request.StartsAtLocal, request.SalesStartAtLocal, request.SalesEndAtLocal, venue.Timezone, now);

        session.VenueId = venue.Id;
        session.City = venue.City;
        session.Timezone = venue.Timezone;
        session.StartsAt = times.StartsAt;
        session.SalesStartAt = times.SalesStartAt;
        session.SalesEndAt = times.SalesEndAt;
        session.HoldTtlSeconds = ResolveTtl(request.HoldTtlSeconds, holds.Value);
        audit.Add(now, "session.updated", "session", session.Id, organizationId);
        await db.SaveChangesAsync(ct);
        return Results.Ok(await LoadSessionDtoAsync(db, session.Id, clock, ct));
    }

    private static async Task<IResult> SetSeatingAsync(Guid sessionId, SeatingRequest request, AppDbContext db, CatalogAccess access,
        ITransactionRunner transactions, IInventoryGuards guards, IClock clock, IAuditWriter audit, CancellationToken ct)
    {
        var organizationId = await access.RequireSessionAdminAsync(sessionId, ct);
        EnsureCurrency(request.Currency);
        var prices = request.TierPrices ?? [];

        await transactions.RunAsync(async token =>
        {
            var session = await LockSessionAsync(db, sessionId, token);
            if (session.SalesMode != SalesMode.ReservedSeating)
                throw ApiErrors.Unprocessable(ErrorCodes.InvalidSelection, "Seating applies only to reserved-seating sessions.");
            if (session.Status == SessionStatus.Cancelled)
                throw ApiErrors.Conflict(ErrorCodes.SessionCancelled, "The session is cancelled.");
            // Satış başladıktan (herhangi bir rezervasyon oluştuktan) sonra plan değiştirilemez.
            if (await guards.SessionHasReservationsAsync(sessionId, token))
                throw ApiErrors.Conflict(ErrorCodes.LayoutLocked, "The seating plan cannot change after reservations exist for this session.");

            var layout = await db.SeatLayouts.AsNoTracking().FirstOrDefaultAsync(l => l.Id == request.LayoutId && l.VenueId == session.VenueId, token)
                ?? throw ApiErrors.Unprocessable(ErrorCodes.InvalidSelection, "The layout does not belong to this session's venue.");
            var layoutSeats = await db.LayoutSeats.AsNoTracking().Where(s => s.LayoutId == layout.Id).ToListAsync(token);

            var validator = new Validator();
            foreach (var tier in layoutSeats.Select(s => s.TierCode).Distinct(StringComparer.Ordinal))
                validator.Check(prices.TryGetValue(tier, out var price) && Money.IsValidPriceMinor(price), $"tierPrices.{tier}", "A valid price in minor units is required for every tier of the layout.");
            foreach (var extra in prices.Keys.Where(k => layoutSeats.All(s => s.TierCode != k)))
                validator.Check(false, $"tierPrices.{extra}", "This tier does not exist in the layout.");
            validator.ThrowIfInvalid();

            await db.SessionSeats.Where(s => s.SessionId == sessionId).ExecuteDeleteAsync(token);
            db.SessionSeats.AddRange(layoutSeats.Select(s => new SessionSeat
            {
                Id = Guid.CreateVersion7(), SessionId = sessionId, Section = s.Section, RowLabel = s.RowLabel, SeatNumber = s.SeatNumber,
                X = s.X, Y = s.Y, TierCode = s.TierCode, PriceMinor = prices[s.TierCode], Status = SeatStatus.Available,
            }));
            session.LayoutId = layout.Id;
            var now = await clock.NowAsync(token);
            audit.Add(now, "session.seating_set", "session", sessionId, organizationId, new { layoutId = layout.Id, layoutVersion = layout.Version, seatCount = layoutSeats.Count });
            await db.SaveChangesAsync(token);
            return 0;
        }, ct);

        return Results.Ok(await LoadSessionDtoAsync(db, sessionId, clock, ct));
    }

    private static async Task<IResult> SetTierPricesAsync(Guid sessionId, TierPricesRequest request, AppDbContext db, CatalogAccess access,
        ITransactionRunner transactions, IClock clock, IAuditWriter audit, CancellationToken ct)
    {
        var organizationId = await access.RequireSessionAdminAsync(sessionId, ct);
        EnsureCurrency(request.Currency);
        var prices = request.TierPrices ?? [];
        var validator = new Validator().Check(prices.Count > 0, "tierPrices", "At least one tier price is required.");
        foreach (var (tier, price) in prices)
            validator.Check(Money.IsValidPriceMinor(price), $"tierPrices.{tier}", "Price must be a non-negative amount in minor units.");
        validator.ThrowIfInvalid();

        await transactions.RunAsync(async token =>
        {
            // Seans satırı FOR UPDATE: eşzamanlı hold istekleri (FOR SHARE) fiyat değişimi bitene kadar bekler.
            var session = await LockSessionAsync(db, sessionId, token);
            if (session.Status == SessionStatus.Cancelled)
                throw ApiErrors.Conflict(ErrorCodes.SessionCancelled, "The session is cancelled.");
            var now = await clock.NowAsync(token);
            var tiers = prices.Keys.ToList();

            var known = await db.SessionSeats.Where(s => s.SessionId == sessionId && tiers.Contains(s.TierCode)).Select(s => s.TierCode).Distinct().ToListAsync(token);
            var unknown = tiers.Except(known, StringComparer.Ordinal).ToList();
            if (unknown.Count > 0)
                throw ApiErrors.Unprocessable(ErrorCodes.InvalidSelection, "Unknown tier for this session.", new Dictionary<string, object?> { ["tiers"] = unknown });

            // Global kilit sırası: seans (alındı) → koltuklar kimlik sırasıyla. Süre dolumu worker'ı ile deadlock'a girmez.
            var tierArray = tiers.ToArray();
            await db.Database.ExecuteSqlAsync(
                $"SELECT 1 FROM inventory.session_seats WHERE session_id = {sessionId} AND tier_code = ANY({tierArray}) ORDER BY id FOR UPDATE", token);

            var lockedTiers = await db.SessionSeats
                .Where(s => s.SessionId == sessionId && tiers.Contains(s.TierCode) &&
                            (s.Status == SeatStatus.Sold || (s.Status == SeatStatus.Held && s.HeldUntil > now)))
                .Select(s => s.TierCode).Distinct().ToListAsync(token);
            if (lockedTiers.Count > 0)
                throw ApiErrors.Conflict(ErrorCodes.PriceLocked, "A tier with held or sold seats cannot be repriced.",
                    new Dictionary<string, object?> { ["tiers"] = lockedTiers.Order(StringComparer.Ordinal).ToArray() });

            foreach (var (tier, price) in prices)
                await db.SessionSeats.Where(s => s.SessionId == sessionId && s.TierCode == tier)
                    .ExecuteUpdateAsync(u => u.SetProperty(s => s.PriceMinor, price), token);
            audit.Add(now, "session.tier_prices_set", "session", sessionId, organizationId, new { tiers = prices });
            await db.SaveChangesAsync(token);
            return 0;
        }, ct);

        return Results.Ok(await LoadSessionDtoAsync(db, sessionId, clock, ct));
    }

    private static async Task<IResult> CreateCategoryAsync(Guid sessionId, CategoryRequest request, AppDbContext db, CatalogAccess access,
        ITransactionRunner transactions, IClock clock, IAuditWriter audit, CancellationToken ct)
    {
        var organizationId = await access.RequireSessionAdminAsync(sessionId, ct);
        ValidateCategory(request);

        await transactions.RunAsync(async token =>
        {
            var session = await LockSessionAsync(db, sessionId, token);
            if (session.SalesMode != SalesMode.GeneralAdmission)
                throw ApiErrors.Unprocessable(ErrorCodes.InvalidSelection, "Ticket categories apply only to general-admission sessions.");
            if (session.Status == SessionStatus.Cancelled)
                throw ApiErrors.Conflict(ErrorCodes.SessionCancelled, "The session is cancelled.");
            var existing = await db.GaCategories.Where(c => c.SessionId == sessionId).Select(c => c.Name).ToListAsync(token);
            if (existing.Count >= MaxCategoriesPerSession)
                throw ApiErrors.Unprocessable(ErrorCodes.ValidationFailed, $"A session can have at most {MaxCategoriesPerSession} categories.");
            if (existing.Contains(request.Name.Trim(), StringComparer.OrdinalIgnoreCase))
                throw ApiErrors.Conflict(ErrorCodes.Conflict, "A category with this name already exists.");

            var now = await clock.NowAsync(token);
            var category = new GaCategory
            {
                Id = Guid.CreateVersion7(), SessionId = sessionId, Name = request.Name.Trim(), PriceMinor = request.PriceMinor,
                Capacity = request.Capacity, SortOrder = existing.Count,
            };
            db.GaCategories.Add(category);
            audit.Add(now, "session.category_created", "ga_category", category.Id, organizationId, new { sessionId, request.Capacity, request.PriceMinor });
            await db.SaveChangesAsync(token);
            return 0;
        }, ct);

        return Results.Ok(await LoadSessionDtoAsync(db, sessionId, clock, ct));
    }

    private static async Task<IResult> UpdateCategoryAsync(Guid sessionId, Guid categoryId, CategoryRequest request, AppDbContext db,
        CatalogAccess access, ITransactionRunner transactions, IClock clock, IAuditWriter audit, CancellationToken ct)
    {
        var organizationId = await access.RequireSessionAdminAsync(sessionId, ct);
        ValidateCategory(request);

        await transactions.RunAsync(async token =>
        {
            var session = await LockSessionAsync(db, sessionId, token);
            if (session.Status == SessionStatus.Cancelled)
                throw ApiErrors.Conflict(ErrorCodes.SessionCancelled, "The session is cancelled.");
            var name = request.Name.Trim();
            if (!await db.GaCategories.AnyAsync(c => c.Id == categoryId && c.SessionId == sessionId, token)) throw ApiErrors.NotFound();
            if (await db.GaCategories.AnyAsync(c => c.SessionId == sessionId && c.Id != categoryId && c.Name.ToLower() == name.ToLower(), token))
                throw ApiErrors.Conflict(ErrorCodes.Conflict, "A category with this name already exists.");

            // Koşullu güncelleme: kapasite, tutulmuş + satılmış adedin altına indirilemez.
            // (CHECK kısıtı son savunmadır; bu koşul ihlali istisnaya çevirmeden reddeder.)
            var changed = await db.GaCategories
                .Where(c => c.Id == categoryId && c.SessionId == sessionId && c.HeldCount + c.SoldCount <= request.Capacity)
                .ExecuteUpdateAsync(u => u
                    .SetProperty(c => c.Capacity, request.Capacity)
                    .SetProperty(c => c.PriceMinor, request.PriceMinor)
                    .SetProperty(c => c.Name, name), token);
            if (changed != 1)
            {
                var committed = await db.GaCategories.Where(c => c.Id == categoryId).Select(c => c.HeldCount + c.SoldCount).FirstAsync(token);
                throw ApiErrors.Conflict(ErrorCodes.CapacityBelowCommitted, "Capacity cannot be reduced below the number of held and sold tickets.",
                    new Dictionary<string, object?> { ["committed"] = committed });
            }
            var now = await clock.NowAsync(token);
            audit.Add(now, "session.category_updated", "ga_category", categoryId, organizationId, new { sessionId, request.Capacity, request.PriceMinor });
            await db.SaveChangesAsync(token);
            return 0;
        }, ct);

        return Results.Ok(await LoadSessionDtoAsync(db, sessionId, clock, ct));
    }

    private static void ValidateCategory(CategoryRequest request)
    {
        EnsureCurrency(request.Currency);
        new Validator()
            .Required(request.Name, "name", 80, 2)
            .Check(Money.IsValidPriceMinor(request.PriceMinor), "priceMinor", "Price must be a non-negative amount in minor units.")
            .Check(request.Capacity is >= 1 and <= 100_000, "capacity", "Capacity must be between 1 and 100000.")
            .ThrowIfInvalid();
    }

    private static async Task<IResult> PublishSessionAsync(Guid sessionId, AppDbContext db, CatalogAccess access,
        ITransactionRunner transactions, IClock clock, IAuditWriter audit, CancellationToken ct)
    {
        var organizationId = await access.RequireSessionAdminAsync(sessionId, ct);
        await transactions.RunAsync(async token =>
        {
            var session = await LockSessionAsync(db, sessionId, token);
            StateMachines.Ensure(StateMachines.CanTransition(session.Status, SessionStatus.Published), session.Status, SessionStatus.Published);

            var parent = await db.Events.AsNoTracking().Include(e => e.Organization).FirstAsync(e => e.Id == session.EventId, token);
            var now = await clock.NowAsync(token);
            var failures = new List<string>();
            if (parent.Organization.Status != OrganizationStatus.Active) failures.Add("organization_not_active");
            if (parent.Status != EventStatus.Published) failures.Add("event_not_published");
            if (parent.ModerationBlockedAt is not null) failures.Add("event_taken_down");
            if (session.StartsAt <= now) failures.Add("session_start_in_past");
            if (!SalesWindow.IsValidDefinition(session.SalesStartAt, session.SalesEndAt, session.StartsAt)) failures.Add("invalid_sales_window");
            var hasInventory = session.SalesMode == SalesMode.ReservedSeating
                ? await db.SessionSeats.AnyAsync(s => s.SessionId == sessionId, token)
                : await db.GaCategories.AnyAsync(c => c.SessionId == sessionId, token);
            if (!hasInventory) failures.Add(session.SalesMode == SalesMode.ReservedSeating ? "seating_not_configured" : "no_ticket_category");
            if (failures.Count > 0)
                throw ApiErrors.Conflict(ErrorCodes.PublishGuardFailed, "The session cannot be published yet.",
                    new Dictionary<string, object?> { ["reasons"] = failures });

            session.Status = SessionStatus.Published;
            audit.Add(now, "session.published", "session", sessionId, organizationId);
            await db.SaveChangesAsync(token);
            return 0;
        }, ct);
        return Results.Ok(await LoadSessionDtoAsync(db, sessionId, clock, ct));
    }

    private static async Task<IResult> SetDemoSimulatorAsync(Guid sessionId, DemoSimulatorRequest request, AppDbContext db, CatalogAccess access,
        IOptions<DemoOptions> demo, IClock clock, IAuditWriter audit, CancellationToken ct)
    {
        var organizationId = await access.RequireSessionAdminAsync(sessionId, ct);
        if (request.Enabled && !demo.Value.SimulatorEnabled)
            throw ApiErrors.Conflict(ErrorCodes.Conflict, "The demo simulator is disabled on this installation.");
        var session = await db.Sessions.FirstAsync(s => s.Id == sessionId, ct);
        session.DemoSimulatorEnabled = request.Enabled;
        audit.Add(await clock.NowAsync(ct), "session.demo_simulator_set", "session", sessionId, organizationId, new { request.Enabled });
        await db.SaveChangesAsync(ct);
        return Results.Ok(await LoadSessionDtoAsync(db, sessionId, clock, ct));
    }

    // --- Yardımcılar -----------------------------------------------------------------------------

    internal static Task<Session> LockSessionAsync(AppDbContext db, Guid sessionId, CancellationToken ct) =>
        db.Sessions.FromSql($"SELECT * FROM catalog.sessions WHERE id = {sessionId} FOR UPDATE").SingleAsync(ct);

    private static void EnsureCurrency(string? currency)
    {
        if (currency is not null && !Money.IsSupportedCurrency(currency))
            throw ApiErrors.BadRequest(ErrorCodes.UnsupportedCurrency, "Only TRY is supported.");
    }

    private static async Task<Venue> RequireOwnVenueAsync(AppDbContext db, Guid venueId, Guid organizationId, CancellationToken ct) =>
        await db.Venues.AsNoTracking().FirstOrDefaultAsync(v => v.Id == venueId && v.OrganizationId == organizationId, ct)
        ?? throw ApiErrors.Unprocessable(ErrorCodes.InvalidSelection, "The venue does not exist in this organization.");

    private static int ResolveTtl(int? requested, HoldOptions holds)
    {
        var ttl = requested ?? holds.DefaultTtlSeconds;
        if (ttl < holds.MinTtlSeconds || ttl > holds.MaxTtlSeconds)
            throw ApiErrors.BadRequest(ErrorCodes.ValidationFailed, "One or more fields are invalid.", new Dictionary<string, object?>
            {
                ["errors"] = new Dictionary<string, string[]> { ["holdTtlSeconds"] = [$"Must be between {holds.MinTtlSeconds} and {holds.MaxTtlSeconds} seconds."] },
            });
        return ttl;
    }

    private static (DateTimeOffset StartsAt, DateTimeOffset SalesStartAt, DateTimeOffset SalesEndAt) ResolveTimes(
        string startsAtLocal, string? salesStartLocal, string? salesEndLocal, string timezone, DateTimeOffset now)
    {
        var startsAt = ParseLocal(startsAtLocal, "startsAtLocal", timezone);
        var salesStart = salesStartLocal is null ? Timestamps.TruncateToMicroseconds(now) : ParseLocal(salesStartLocal, "salesStartAtLocal", timezone);
        var salesEnd = salesEndLocal is null ? startsAt : ParseLocal(salesEndLocal, "salesEndAtLocal", timezone);

        new Validator()
            .Check(startsAt > now, "startsAtLocal", "The session must start in the future.")
            .Check(salesEnd <= startsAt, "salesEndAtLocal", "Sales must end no later than the session start.")
            .Check(salesStart < salesEnd, "salesStartAtLocal", "Sales must start before they end.")
            .ThrowIfInvalid();
        return (startsAt, salesStart, salesEnd);
    }

    private static readonly string[] LocalFormats = ["yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd'T'HH:mm:ss"];

    private static DateTimeOffset ParseLocal(string? value, string field, string timezone)
    {
        if (!DateTime.TryParseExact(value, LocalFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
            throw ApiErrors.BadRequest(ErrorCodes.ValidationFailed, "One or more fields are invalid.", new Dictionary<string, object?>
            {
                ["errors"] = new Dictionary<string, string[]> { [field] = ["Use venue-local time in the form yyyy-MM-ddTHH:mm (no offset)."] },
            });
        var resolved = LocalTimeResolver.Resolve(local, timezone);
        if (!resolved.Success)
            throw ApiErrors.BadRequest(resolved.ErrorCode!, resolved.ErrorCode == ErrorCodes.InvalidLocalTime
                ? $"'{value}' does not exist or is ambiguous in {timezone} (daylight-saving transition)."
                : "The venue timezone is not valid.", new Dictionary<string, object?> { ["field"] = field });
        return resolved.Utc;
    }

    internal static string FormatLocal(DateTimeOffset instant, string timezone) =>
        LocalTimeResolver.ToLocal(instant, timezone).ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture);

    private static OrganizerEventDto ToDto(Event e) => new(e.Id, e.OrganizationId, e.Title, e.Slug, e.Description, e.Category, e.Status,
        e.ModerationBlockedAt is not null, e.IsDemo,
        [.. e.Sessions.OrderBy(s => s.StartsAt).ThenBy(s => s.Id).Select(s => new OrganizerSessionSummaryDto(
            s.Id, s.StartsAt, FormatLocal(s.StartsAt, s.Timezone), s.Timezone, s.Venue?.Name ?? "", s.City, s.SalesMode, s.Status))]);

    internal static async Task<OrganizerSessionDto> LoadSessionDtoAsync(AppDbContext db, Guid sessionId, IClock clock, CancellationToken ct)
    {
        var session = await db.Sessions.AsNoTracking().Include(s => s.Venue).Include(s => s.Event).FirstOrDefaultAsync(s => s.Id == sessionId, ct)
            ?? throw ApiErrors.NotFound();
        var now = await clock.NowAsync(ct);

        SeatCountsDto? seats = null;
        IReadOnlyList<TierDto> tiers = [];
        IReadOnlyList<OrganizerCategoryDto> categories = [];
        if (session.SalesMode == SalesMode.ReservedSeating)
        {
            var rows = await db.SessionSeats.AsNoTracking().Where(s => s.SessionId == sessionId)
                .GroupBy(s => new { s.TierCode, s.PriceMinor })
                .Select(g => new
                {
                    g.Key.TierCode, g.Key.PriceMinor, Total = g.Count(),
                    Sold = g.Count(s => s.Status == SeatStatus.Sold),
                    Held = g.Count(s => s.Status == SeatStatus.Held && s.HeldUntil > now),
                }).ToListAsync(ct);
            if (rows.Count > 0)
            {
                var total = rows.Sum(r => r.Total);
                var sold = rows.Sum(r => r.Sold);
                var held = rows.Sum(r => r.Held);
                seats = new SeatCountsDto(total, total - sold - held, held, sold);
                tiers = [.. rows.GroupBy(r => r.TierCode).OrderBy(g => g.Key, StringComparer.Ordinal)
                    .Select(g => new TierDto(g.Key, g.Min(r => r.PriceMinor), g.Sum(r => r.Total)))];
            }
        }
        else
        {
            categories = await db.GaCategories.AsNoTracking().Where(c => c.SessionId == sessionId).OrderBy(c => c.SortOrder).ThenBy(c => c.Id)
                .Select(c => new OrganizerCategoryDto(c.Id, c.Name, c.PriceMinor, c.Capacity, c.HeldCount, c.SoldCount, c.Capacity - c.HeldCount - c.SoldCount))
                .ToListAsync(ct);
        }

        return new OrganizerSessionDto(session.Id, session.EventId, session.Event.Title, session.VenueId, session.Venue.Name, session.City,
            session.Timezone, session.StartsAt, FormatLocal(session.StartsAt, session.Timezone), session.SalesStartAt, session.SalesEndAt,
            session.SalesMode, session.Status, session.HoldTtlSeconds, session.LayoutId, session.DemoSimulatorEnabled, Money.SupportedCurrency,
            seats, tiers, categories);
    }
}

/// <summary>Bölüm tanımlarından koltuk ve plan koordinatı üretir. Koordinat birimi: onda bir koltuk.</summary>
public static class LayoutGenerator
{
    private const int Unit = 10;
    private const int SectionGapRows = 2;

    public static List<LayoutSeat> Generate(IReadOnlyList<LayoutSectionRequest> sections)
    {
        var widest = sections.Max(s => s.SeatsPerRow + (s.AisleAfter is null ? 0 : 1));
        var seats = new List<LayoutSeat>();
        var rowOffset = 0;
        foreach (var section in sections)
        {
            var width = section.SeatsPerRow + (section.AisleAfter is null ? 0 : 1);
            var indent = (widest - width) * Unit / 2;
            for (var row = 0; row < section.Rows; row++)
            {
                for (var number = 1; number <= section.SeatsPerRow; number++)
                {
                    var column = number - 1 + (section.AisleAfter is { } aisle && number > aisle ? 1 : 0);
                    seats.Add(new LayoutSeat
                    {
                        Id = Guid.CreateVersion7(), Section = section.Name.Trim(), RowLabel = RowLabel(row), SeatNumber = number,
                        X = indent + column * Unit, Y = (rowOffset + row) * Unit, TierCode = section.TierCode,
                    });
                }
            }
            rowOffset += section.Rows + SectionGapRows;
        }
        return seats;
    }

    /// <summary>A..Z, sonra AA, AB...</summary>
    public static string RowLabel(int index)
    {
        var label = "";
        for (var i = index; i >= 0; i = i / 26 - 1)
            label = (char)('A' + i % 26) + label;
        return label;
    }
}
