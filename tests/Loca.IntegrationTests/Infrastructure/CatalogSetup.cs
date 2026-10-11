using System.Globalization;
using System.Net;
using Loca.Domain;

namespace Loca.IntegrationTests.Infrastructure;

public sealed record SeatedSessionSetup(Guid VenueId, Guid LayoutId, Guid EventId, string EventSlug, Guid SessionId, IReadOnlyList<Guid> SeatIds);
public sealed record GaSessionSetup(Guid VenueId, Guid EventId, string EventSlug, Guid SessionId, IReadOnlyList<Guid> CategoryIds);

/// <summary>Testlerin katalog düzeneğini gerçek organizatör API'si üzerinden kuran yardımcılar.</summary>
public static class CatalogSetup
{
    public const string Timezone = "Europe/Istanbul";

    /// <summary>Kademe kodları büyük/küçük harfe duyarlıdır; anonim nesne camelCase'e çevrileceği için sözlük kullanılır.</summary>
    public static Dictionary<string, long> Tiers(params (string Tier, long PriceMinor)[] prices) =>
        prices.ToDictionary(p => p.Tier, p => p.PriceMinor, StringComparer.Ordinal);

    public static string Local(DateTimeOffset instant, string timezone = Timezone) =>
        LocalTimeResolver.ToLocal(instant, timezone).ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture);

    public static async Task<Guid> VenueAsync(TestClient organizer, Guid organizationId, string city = "İstanbul", string timezone = Timezone, string name = "Demo Salon")
    {
        var response = (await organizer.PostAsync($"/api/organizer/organizations/{organizationId}/venues",
            new { name, city, address = "Demo Caddesi No: 1", timezone })).ShouldBe(HttpStatusCode.Created);
        return response.Json["id"]!.GetValue<Guid>();
    }

    public static async Task<Guid> LayoutAsync(TestClient organizer, Guid venueId, int rows = 2, int seatsPerRow = 5, bool twoTiers = false)
    {
        object[] sections = twoTiers
            ? [new { name = "Salon", tierCode = "A", rows, seatsPerRow }, new { name = "Balkon", tierCode = "B", rows = 1, seatsPerRow }]
            : [new { name = "Salon", tierCode = "A", rows, seatsPerRow }];
        var response = (await organizer.PostAsync($"/api/organizer/venues/{venueId}/layouts", new { name = "Demo Plan", sections })).ShouldBe(HttpStatusCode.Created);
        return response.Json["id"]!.GetValue<Guid>();
    }

    public static async Task<(Guid Id, string Slug)> EventAsync(TestClient organizer, Guid organizationId, string title = "Demo Konser",
        string category = "Concert", bool publish = true)
    {
        var response = (await organizer.PostAsync($"/api/organizer/organizations/{organizationId}/events",
            new { title, description = "Sentetik test etkinliği açıklaması.", category })).ShouldBe(HttpStatusCode.Created);
        var id = response.Json["id"]!.GetValue<Guid>();
        if (publish) (await organizer.PostAsync($"/api/organizer/events/{id}/publish")).ShouldBe(HttpStatusCode.OK);
        return (id, response.Json["slug"]!.GetValue<string>());
    }

    public static async Task<Guid> DraftSessionAsync(TestClient organizer, Guid eventId, Guid venueId, DateTimeOffset startsAt, string salesMode,
        int? holdTtlSeconds = null, DateTimeOffset? salesStartAt = null, DateTimeOffset? salesEndAt = null)
    {
        var response = (await organizer.PostAsync($"/api/organizer/events/{eventId}/sessions", new
        {
            venueId,
            startsAtLocal = Local(startsAt),
            salesStartAtLocal = salesStartAt is null ? null : Local(salesStartAt.Value),
            salesEndAtLocal = salesEndAt is null ? null : Local(salesEndAt.Value),
            salesMode,
            holdTtlSeconds,
        })).ShouldBe(HttpStatusCode.Created);
        return response.Json["id"]!.GetValue<Guid>();
    }

    /// <summary>Yayınlanmış, satışta, koltuklu seans (varsayılan 2 sıra × 5 koltuk, kademe A = 450,00 TRY).</summary>
    public static async Task<SeatedSessionSetup> SeatedSessionAsync(TestClient organizer, Guid organizationId, DateTimeOffset startsAt,
        int rows = 2, int seatsPerRow = 5, long priceMinor = 45000, bool publish = true, string title = "Demo Konser",
        int? holdTtlSeconds = null, bool twoTiers = false, string city = "İstanbul", string category = "Concert", bool demoSimulator = true)
    {
        var venueId = await VenueAsync(organizer, organizationId, city);
        var layoutId = await LayoutAsync(organizer, venueId, rows, seatsPerRow, twoTiers);
        var (eventId, slug) = await EventAsync(organizer, organizationId, title, category);
        var sessionId = await DraftSessionAsync(organizer, eventId, venueId, startsAt, "ReservedSeating", holdTtlSeconds);
        var tierPrices = twoTiers ? new Dictionary<string, long> { ["A"] = priceMinor, ["B"] = priceMinor / 2 } : new Dictionary<string, long> { ["A"] = priceMinor };
        (await organizer.PutAsync($"/api/organizer/sessions/{sessionId}/seating", new { layoutId, tierPrices })).ShouldBe(HttpStatusCode.OK);
        if (publish) (await organizer.PostAsync($"/api/organizer/sessions/{sessionId}/publish")).ShouldBe(HttpStatusCode.OK);
        if (demoSimulator) (await organizer.PutAsync($"/api/organizer/sessions/{sessionId}/demo-simulator", new { enabled = true })).ShouldBe(HttpStatusCode.OK);

        IReadOnlyList<Guid> seatIds = [];
        if (publish)
        {
            var seats = (await organizer.GetAsync($"/api/catalog/sessions/{sessionId}/seats")).ShouldBe(HttpStatusCode.OK);
            seatIds = [.. seats.Json["seats"]!.AsArray().Select(s => s!["id"]!.GetValue<Guid>())];
        }
        return new SeatedSessionSetup(venueId, layoutId, eventId, slug, sessionId, seatIds);
    }

    /// <summary>Yayınlanmış, satışta, genel giriş seansı.</summary>
    public static async Task<GaSessionSetup> GaSessionAsync(TestClient organizer, Guid organizationId, DateTimeOffset startsAt,
        (string Name, long PriceMinor, int Capacity)[]? categories = null, bool publish = true, string title = "Demo Festival",
        int? holdTtlSeconds = null, string city = "İstanbul", string category = "Festival", bool demoSimulator = true)
    {
        categories ??= [("Genel Giriş", 25000, 10)];
        var venueId = await VenueAsync(organizer, organizationId, city);
        var (eventId, slug) = await EventAsync(organizer, organizationId, title, category);
        var sessionId = await DraftSessionAsync(organizer, eventId, venueId, startsAt, "GeneralAdmission", holdTtlSeconds);
        ApiResponse? last = null;
        foreach (var (name, priceMinor, capacity) in categories)
            last = (await organizer.PostAsync($"/api/organizer/sessions/{sessionId}/categories", new { name, priceMinor, capacity })).ShouldBe(HttpStatusCode.OK);
        if (publish) (await organizer.PostAsync($"/api/organizer/sessions/{sessionId}/publish")).ShouldBe(HttpStatusCode.OK);
        if (demoSimulator) (await organizer.PutAsync($"/api/organizer/sessions/{sessionId}/demo-simulator", new { enabled = true })).ShouldBe(HttpStatusCode.OK);
        var ids = last!.Json["categories"]!.AsArray().Select(c => c!["id"]!.GetValue<Guid>()).ToList();
        return new GaSessionSetup(venueId, eventId, slug, sessionId, ids);
    }
}
