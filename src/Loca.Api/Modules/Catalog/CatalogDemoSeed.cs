using Loca.Api.Modules.Inventory;
using Loca.Api.Modules.Operations;
using Loca.Domain;
using Microsoft.EntityFrameworkCore;

namespace Loca.Api.Modules.Catalog;

/// <summary>
/// Sentetik demo kataloğu. Etkinlik slug'ı ile idempotenttir; tarihler seed anına göre görelidir.
/// Tüm adlar "(Demo)" etiketlidir ve gerçek kişi/kurumla ilgisi yoktur.
/// </summary>
internal sealed class CatalogDemoSeed : IDemoSeedContributor
{
    public int Order => 10;

    public async Task SeedAsync(SeedContext context, CancellationToken ct)
    {
        var db = context.Db;
        var now = context.Now;

        var salon = await EnsureVenueAsync(context, context.KumruOrganizationId, "Kumru Sahne Salonu (Demo)", "İstanbul", "Demo Caddesi No: 1, Kadıköy", ct);
        var bahce = await EnsureVenueAsync(context, context.KumruOrganizationId, "Kumru Açık Hava Bahçesi (Demo)", "İstanbul", "Demo Sokağı No: 7, Kadıköy", ct);
        var kiyi = await EnsureVenueAsync(context, context.MartiOrganizationId, "Martı Kıyı Sahnesi (Demo)", "İzmir", "Demo Kordon No: 12, Alsancak", ct);

        var salonLayout = await EnsureLayoutAsync(context, salon, "Ana Salon",
        [
            new LayoutSectionRequest("Salon", "A", 6, 14, 7),
            new LayoutSectionRequest("Balkon", "B", 3, 12, 6),
        ], ct);
        var kiyiLayout = await EnsureLayoutAsync(context, kiyi, "Kıyı Düzeni", [new LayoutSectionRequest("Tribün", "A", 5, 10, 5)], ct);

        await EnsureSeatedAsync(context, "demo-sis-vadisi-senfonisi", context.KumruOrganizationId, "Sis Vadisi Senfonisi (Demo)",
            "Sentetik bir senfoni akşamı. Bu etkinlik demo verisidir; gerçek bir etkinliği temsil etmez.", EventCategory.Concert,
            salon, salonLayout, now.AddDays(21), new() { ["A"] = 45000, ["B"] = 30000 }, demoSimulator: true, ct);

        await EnsureGeneralAdmissionAsync(context, "demo-acik-hava-caz-gecesi", context.KumruOrganizationId, "Açık Hava Caz Gecesi (Demo)",
            "Sentetik bir açık hava konseri. Bu etkinlik demo verisidir.", EventCategory.Festival, bahce, now.AddDays(14),
            [("Genel Giriş", 25000, 300), ("Sahne Önü", 40000, 80), ("Öğrenci", 15000, 60)], demoSimulator: true, ct);

        // Check-in penceresi seed anında açık olan seans: kapı ekranını denemek için.
        await EnsureGeneralAdmissionAsync(context, "demo-bu-aksam-stand-up", context.KumruOrganizationId, "Bu Akşam Stand-up (Demo)",
            "Kapı kontrolünü denemek için yakın başlangıçlı sentetik seans. Bu etkinlik demo verisidir.", EventCategory.Standup, bahce, now.AddHours(2),
            [("Genel Giriş", 20000, 120)], demoSimulator: true, ct);

        await EnsureSeatedAsync(context, "demo-kiyi-tiyatrosu-firtina", context.MartiOrganizationId, "Kıyı Tiyatrosu: Fırtına (Demo)",
            "Sentetik bir tiyatro gösterimi. Bu etkinlik demo verisidir.", EventCategory.Theatre,
            kiyi, kiyiLayout, now.AddDays(30), new() { ["A"] = 35000 }, demoSimulator: false, ct);
    }

    private static async Task<Venue> EnsureVenueAsync(SeedContext context, Guid organizationId, string name, string city, string address, CancellationToken ct)
    {
        var existing = await context.Db.Venues.FirstOrDefaultAsync(v => v.OrganizationId == organizationId && v.Name == name, ct);
        if (existing is not null) { context.Report.Existing++; return existing; }
        var venue = new Venue { Id = Guid.CreateVersion7(), OrganizationId = organizationId, Name = name, City = city, Address = address, Timezone = "Europe/Istanbul", CreatedAt = context.Now };
        context.Db.Venues.Add(venue);
        await context.Db.SaveChangesAsync(ct);
        context.Report.Created++;
        return venue;
    }

    private static async Task<SeatLayout> EnsureLayoutAsync(SeedContext context, Venue venue, string name, IReadOnlyList<LayoutSectionRequest> sections, CancellationToken ct)
    {
        var existing = await context.Db.SeatLayouts.FirstOrDefaultAsync(l => l.VenueId == venue.Id && l.Version == 1, ct);
        if (existing is not null) { context.Report.Existing++; return existing; }
        var seats = LayoutGenerator.Generate(sections);
        var layout = new SeatLayout { Id = Guid.CreateVersion7(), VenueId = venue.Id, Version = 1, Name = name, SeatCount = seats.Count, CreatedAt = context.Now };
        foreach (var seat in seats) seat.LayoutId = layout.Id;
        context.Db.SeatLayouts.Add(layout);
        context.Db.LayoutSeats.AddRange(seats);
        await context.Db.SaveChangesAsync(ct);
        context.Report.Created++;
        return layout;
    }

    private static async Task<Session?> EnsureEventAndSessionAsync(SeedContext context, string slug, Guid organizationId, string title, string description,
        EventCategory category, Venue venue, DateTimeOffset startsAt, SalesMode mode, Guid? layoutId, bool demoSimulator, CancellationToken ct)
    {
        if (await context.Db.Events.AnyAsync(e => e.Slug == slug, ct)) { context.Report.Existing++; return null; }
        var created = new Event
        {
            Id = Guid.CreateVersion7(), OrganizationId = organizationId, Title = title, Slug = slug, Description = description, Category = category,
            Status = EventStatus.Published, IsDemo = true, CreatedAt = context.Now, UpdatedAt = context.Now,
        };
        var start = Timestamps.TruncateToMicroseconds(startsAt);
        var session = new Session
        {
            Id = Guid.CreateVersion7(), EventId = created.Id, OrganizationId = organizationId, VenueId = venue.Id, City = venue.City,
            Timezone = venue.Timezone, StartsAt = start, SalesStartAt = Timestamps.TruncateToMicroseconds(context.Now.AddMinutes(-1)), SalesEndAt = start,
            SalesMode = mode, Status = SessionStatus.Published, HoldTtlSeconds = HoldPolicy.DefaultTtlSeconds, LayoutId = layoutId,
            DemoSimulatorEnabled = demoSimulator, CreatedAt = context.Now,
        };
        context.Db.Events.Add(created);
        context.Db.Sessions.Add(session);
        context.Report.Created++;
        return session;
    }

    private static async Task EnsureSeatedAsync(SeedContext context, string slug, Guid organizationId, string title, string description, EventCategory category,
        Venue venue, SeatLayout layout, DateTimeOffset startsAt, Dictionary<string, long> tierPrices, bool demoSimulator, CancellationToken ct)
    {
        var session = await EnsureEventAndSessionAsync(context, slug, organizationId, title, description, category, venue, startsAt,
            SalesMode.ReservedSeating, layout.Id, demoSimulator, ct);
        if (session is null) return;
        var layoutSeats = await context.Db.LayoutSeats.AsNoTracking().Where(s => s.LayoutId == layout.Id).ToListAsync(ct);
        context.Db.SessionSeats.AddRange(layoutSeats.Select(s => new SessionSeat
        {
            Id = Guid.CreateVersion7(), SessionId = session.Id, Section = s.Section, RowLabel = s.RowLabel, SeatNumber = s.SeatNumber,
            X = s.X, Y = s.Y, TierCode = s.TierCode, PriceMinor = tierPrices[s.TierCode], Status = SeatStatus.Available,
        }));
        await context.Db.SaveChangesAsync(ct);
    }

    private static async Task EnsureGeneralAdmissionAsync(SeedContext context, string slug, Guid organizationId, string title, string description,
        EventCategory category, Venue venue, DateTimeOffset startsAt, (string Name, long PriceMinor, int Capacity)[] categories, bool demoSimulator, CancellationToken ct)
    {
        var session = await EnsureEventAndSessionAsync(context, slug, organizationId, title, description, category, venue, startsAt,
            SalesMode.GeneralAdmission, null, demoSimulator, ct);
        if (session is null) return;
        context.Db.GaCategories.AddRange(categories.Select((c, index) => new GaCategory
        {
            Id = Guid.CreateVersion7(), SessionId = session.Id, Name = c.Name, PriceMinor = c.PriceMinor, Capacity = c.Capacity, SortOrder = index,
        }));
        await context.Db.SaveChangesAsync(ct);
    }
}
