using System.Net;
using Loca.Api.Modules.Operations;
using Loca.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Loca.IntegrationTests.Stage3;

public sealed class CatalogVisibilityTests(PostgresFixture postgres) : IntegrationTest(postgres)
{
    private async Task<IReadOnlyList<Guid>> PublicSessionIdsAsync()
    {
        var page = (await Factory.NewClient().GetAsync("/api/catalog/sessions?limit=50")).ShouldBe(HttpStatusCode.OK);
        return [.. page.Json["items"]!.AsArray().Select(i => i!["sessionId"]!.GetValue<Guid>())];
    }

    private async Task AssertHiddenAsync(Guid sessionId, string eventSlug)
    {
        var anonymous = Factory.NewClient();
        Assert.DoesNotContain(sessionId, await PublicSessionIdsAsync());
        (await anonymous.GetAsync($"/api/catalog/sessions/{sessionId}")).ShouldFail(HttpStatusCode.NotFound, "not_found");
        (await anonymous.GetAsync($"/api/catalog/sessions/{sessionId}/seats")).ShouldFail(HttpStatusCode.NotFound, "not_found");
        var eventResponse = await anonymous.GetAsync($"/api/catalog/events/{eventSlug}");
        if (eventResponse.Status == HttpStatusCode.OK)
            Assert.DoesNotContain(eventResponse.Json["sessions"]!.AsArray(), s => s!["sessionId"]!.GetValue<Guid>() == sessionId);
        else
            eventResponse.ShouldFail(HttpStatusCode.NotFound, "not_found");
    }

    [Fact]
    public async Task Published_session_of_a_published_event_in_an_active_organization_is_public()
    {
        var (organizer, _, organizationId) = await OrganizerAsync("org-a@loca.test", "Kumru Sahne");
        var setup = await CatalogSetup.SeatedSessionAsync(organizer, organizationId, Clock.Now.AddDays(10));

        Assert.Contains(setup.SessionId, await PublicSessionIdsAsync());
        var detail = (await Factory.NewClient().GetAsync($"/api/catalog/sessions/{setup.SessionId}")).ShouldBe(HttpStatusCode.OK);
        Assert.Equal("Open", detail.Json["salesState"]!.GetValue<string>());
        Assert.Equal(10, detail.Json["seats"]!["total"]!.GetValue<int>());
        Assert.Equal(10, detail.Json["seats"]!["available"]!.GetValue<int>());
        Assert.Equal("TRY", detail.Json["currency"]!.GetValue<string>());
        Assert.Equal("Europe/Istanbul", detail.Json["timezone"]!.GetValue<string>());
        Assert.Equal(CatalogSetup.Local(Clock.Now.AddDays(10)), detail.Json["startsAtLocal"]!.GetValue<string>());
        Assert.Equal(6, detail.Json["maxTicketsPerHold"]!.GetValue<int>());
    }

    [Fact]
    public async Task Draft_session_is_not_public()
    {
        var (organizer, _, organizationId) = await OrganizerAsync("org-a@loca.test", "Kumru Sahne");
        var setup = await CatalogSetup.SeatedSessionAsync(organizer, organizationId, Clock.Now.AddDays(10), publish: false);
        await AssertHiddenAsync(setup.SessionId, setup.EventSlug);
    }

    [Fact]
    public async Task Draft_event_hides_its_sessions_and_itself()
    {
        var (organizer, _, organizationId) = await OrganizerAsync("org-a@loca.test", "Kumru Sahne");
        var setup = await CatalogSetup.SeatedSessionAsync(organizer, organizationId, Clock.Now.AddDays(10));
        // Yayınlanmış seansı olan etkinlik taslağa dönemez; sızıntı senaryosu veritabanı düzeyinde kurulur.
        await Database.ExecuteAsAdminAsync("UPDATE catalog.events SET status = 'Draft' WHERE id = @id", new NpgsqlParameter("id", setup.EventId));
        await AssertHiddenAsync(setup.SessionId, setup.EventSlug);
    }

    [Fact]
    public async Task Suspended_organization_and_taken_down_event_disappear_and_takedown_can_be_cleared()
    {
        var (organizer, _, organizationId) = await OrganizerAsync("org-a@loca.test", "Kumru Sahne");
        var setup = await CatalogSetup.SeatedSessionAsync(organizer, organizationId, Clock.Now.AddDays(10));
        var admin = await SignedInPlatformAdminAsync();

        (await organizer.PostAsync($"/api/admin/events/{setup.EventId}/takedown")).ShouldFail(HttpStatusCode.Forbidden, "forbidden");
        (await admin.PostAsync($"/api/admin/events/{setup.EventId}/takedown")).ShouldBe(HttpStatusCode.OK);
        await AssertHiddenAsync(setup.SessionId, setup.EventSlug);
        (await admin.PostAsync($"/api/admin/events/{setup.EventId}/clear-takedown")).ShouldBe(HttpStatusCode.OK);
        Assert.Contains(setup.SessionId, await PublicSessionIdsAsync());

        (await admin.PostAsync($"/api/admin/organizations/{organizationId}/suspend")).ShouldBe(HttpStatusCode.OK);
        await AssertHiddenAsync(setup.SessionId, setup.EventSlug);
    }

    [Fact]
    public async Task Started_and_cancelled_sessions_are_not_listed()
    {
        var (organizer, _, organizationId) = await OrganizerAsync("org-a@loca.test", "Kumru Sahne");
        var soon = await CatalogSetup.SeatedSessionAsync(organizer, organizationId, Clock.Now.AddHours(5), title: "Yakın Seans");
        var cancelled = await CatalogSetup.SeatedSessionAsync(organizer, organizationId, Clock.Now.AddDays(10), title: "İptal Seans");
        await Database.ExecuteAsAdminAsync("UPDATE catalog.sessions SET status = 'Cancelled' WHERE id = @id", new NpgsqlParameter("id", cancelled.SessionId));

        Assert.Contains(soon.SessionId, await PublicSessionIdsAsync());
        await AssertHiddenAsync(cancelled.SessionId, cancelled.EventSlug);

        Clock.Advance(TimeSpan.FromHours(5)); // now == startsAt: seans başlamıştır
        await AssertHiddenAsync(soon.SessionId, soon.EventSlug);
    }

    [Fact]
    public async Task Public_responses_are_cacheable_without_cookies_and_contain_no_private_fields()
    {
        var (organizer, _, organizationId) = await OrganizerAsync("org-a@loca.test", "Kumru Sahne");
        var setup = await CatalogSetup.SeatedSessionAsync(organizer, organizationId, Clock.Now.AddDays(10));
        var anonymous = Factory.NewClient();

        var list = (await anonymous.GetAsync("/api/catalog/sessions")).ShouldBe(HttpStatusCode.OK);
        Assert.Equal("public, max-age=30", list.Header("Cache-Control"));
        Assert.Empty(list.SetCookies); // GET, antiforgery çerezi üretmez ve önbellek başlığını bozmaz
        Assert.Null(list.Header("Pragma"));

        var filters = (await anonymous.GetAsync("/api/catalog/filters")).ShouldBe(HttpStatusCode.OK);
        Assert.Equal("public, max-age=30", filters.Header("Cache-Control"));
        Assert.Empty(filters.SetCookies);

        var seats = (await anonymous.GetAsync($"/api/catalog/sessions/{setup.SessionId}/seats")).ShouldBe(HttpStatusCode.OK);
        Assert.Equal("no-store", seats.Header("Cache-Control"));
        foreach (var forbidden in new[] { "holdReservationId", "heldUntil", "token", "email", "organizationId", "userId" })
        {
            Assert.DoesNotContain(forbidden, seats.Text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(forbidden, list.Text, StringComparison.OrdinalIgnoreCase);
        }
    }
}

public sealed class CatalogOwnershipTests(PostgresFixture postgres) : IntegrationTest(postgres)
{
    [Fact]
    public async Task Organizer_cannot_read_or_modify_another_organizations_catalog()
    {
        var (organizerA, _, organizationA) = await OrganizerAsync("org-a@loca.test", "Kumru Sahne");
        var (organizerB, _, organizationB) = await OrganizerAsync("org-b@loca.test", "Martı Etkinlik");
        var b = await CatalogSetup.SeatedSessionAsync(organizerB, organizationB, Clock.Now.AddDays(10), publish: false);
        var venueA = await CatalogSetup.VenueAsync(organizerA, organizationA);
        var layoutA = await CatalogSetup.LayoutAsync(organizerA, venueA);

        var attempts = new (string Name, Func<Task<ApiResponse>> Call)[]
        {
            ("list venues", () => organizerA.GetAsync($"/api/organizer/organizations/{organizationB}/venues")),
            ("create venue", () => organizerA.PostAsync($"/api/organizer/organizations/{organizationB}/venues", new { name = "Sızma", city = "İzmir", address = "Adres 1", timezone = "Europe/Istanbul" })),
            ("list layouts", () => organizerA.GetAsync($"/api/organizer/venues/{b.VenueId}/layouts")),
            ("create layout", () => organizerA.PostAsync($"/api/organizer/venues/{b.VenueId}/layouts", new { name = "Sızma", sections = new[] { new { name = "S", tierCode = "A", rows = 1, seatsPerRow = 1 } } })),
            ("layout seats", () => organizerA.GetAsync($"/api/organizer/layouts/{b.LayoutId}/seats")),
            ("list events", () => organizerA.GetAsync($"/api/organizer/organizations/{organizationB}/events")),
            ("create event", () => organizerA.PostAsync($"/api/organizer/organizations/{organizationB}/events", new { title = "Sızma", description = "Açıklama metni yeterli.", category = "Other" })),
            ("get event", () => organizerA.GetAsync($"/api/organizer/events/{b.EventId}")),
            ("update event", () => organizerA.PutAsync($"/api/organizer/events/{b.EventId}", new { title = "Ele Geçirildi", description = "Açıklama metni yeterli.", category = "Other" })),
            ("unpublish event", () => organizerA.PostAsync($"/api/organizer/events/{b.EventId}/unpublish")),
            ("create session", () => organizerA.PostAsync($"/api/organizer/events/{b.EventId}/sessions", new { venueId = venueA, startsAtLocal = CatalogSetup.Local(Clock.Now.AddDays(5)), salesMode = "GeneralAdmission" })),
            ("get session", () => organizerA.GetAsync($"/api/organizer/sessions/{b.SessionId}")),
            ("update session", () => organizerA.PutAsync($"/api/organizer/sessions/{b.SessionId}", new { venueId = venueA, startsAtLocal = CatalogSetup.Local(Clock.Now.AddDays(5)) })),
            ("set seating", () => organizerA.PutAsync($"/api/organizer/sessions/{b.SessionId}/seating", new { layoutId = layoutA, tierPrices = CatalogSetup.Tiers(("A", 1)) })),
            ("set prices", () => organizerA.PutAsync($"/api/organizer/sessions/{b.SessionId}/tier-prices", new { tierPrices = CatalogSetup.Tiers(("A", 1)) })),
            ("create category", () => organizerA.PostAsync($"/api/organizer/sessions/{b.SessionId}/categories", new { name = "Sızma", priceMinor = 1, capacity = 1 })),
            ("publish session", () => organizerA.PostAsync($"/api/organizer/sessions/{b.SessionId}/publish")),
            ("demo simulator", () => organizerA.PutAsync($"/api/organizer/sessions/{b.SessionId}/demo-simulator", new { enabled = true })),
        };
        foreach (var (name, call) in attempts)
        {
            var response = await call();
            Assert.True(response.Status == HttpStatusCode.NotFound && response.Code == "not_found", $"{name}: expected 404 not_found, got {(int)response.Status} {response.Code}");
        }

        // B'nin verisi değişmedi.
        var session = (await organizerB.GetAsync($"/api/organizer/sessions/{b.SessionId}")).ShouldBe(HttpStatusCode.OK);
        Assert.Equal("Draft", session.Json["status"]!.GetValue<string>());
        Assert.Equal(45000, session.Json["tiers"]![0]!["priceMinor"]!.GetValue<long>());
        Assert.Equal("Demo Konser", (await organizerB.GetAsync($"/api/organizer/events/{b.EventId}")).Json["title"]!.GetValue<string>());
        Assert.Equal(1, await Database.ScalarAsAdminAsync<long>("SELECT count(*) FROM catalog.venues WHERE organization_id = @o", new NpgsqlParameter("o", organizationB)));
        Assert.Equal(1, await Database.ScalarAsAdminAsync<long>("SELECT count(*) FROM catalog.sessions WHERE organization_id = @o", new NpgsqlParameter("o", organizationB)));
    }

    [Fact]
    public async Task Session_cannot_use_a_venue_or_layout_from_another_organization()
    {
        var (organizerA, _, organizationA) = await OrganizerAsync("org-a@loca.test", "Kumru Sahne");
        var (organizerB, _, organizationB) = await OrganizerAsync("org-b@loca.test", "Martı Etkinlik");
        var venueB = await CatalogSetup.VenueAsync(organizerB, organizationB);
        var layoutB = await CatalogSetup.LayoutAsync(organizerB, venueB);
        var venueA = await CatalogSetup.VenueAsync(organizerA, organizationA);
        var (eventA, _) = await CatalogSetup.EventAsync(organizerA, organizationA);

        (await organizerA.PostAsync($"/api/organizer/events/{eventA}/sessions",
            new { venueId = venueB, startsAtLocal = CatalogSetup.Local(Clock.Now.AddDays(5)), salesMode = "ReservedSeating" }))
            .ShouldFail(HttpStatusCode.UnprocessableEntity, "invalid_selection");

        var sessionA = await CatalogSetup.DraftSessionAsync(organizerA, eventA, venueA, Clock.Now.AddDays(5), "ReservedSeating");
        (await organizerA.PutAsync($"/api/organizer/sessions/{sessionA}/seating", new { layoutId = layoutB, tierPrices = CatalogSetup.Tiers(("A", 1000)) }))
            .ShouldFail(HttpStatusCode.UnprocessableEntity, "invalid_selection");
    }

    [Fact]
    public async Task Gate_staff_cannot_manage_the_catalog()
    {
        var (organizer, _, organizationId) = await OrganizerAsync("org-a@loca.test", "Kumru Sahne");
        var (gate, _) = await SignedInAsync("kapi@loca.test");
        (await organizer.PostAsync($"/api/organizer/organizations/{organizationId}/members", new { email = "kapi@loca.test", role = "GateStaff" })).ShouldBe(HttpStatusCode.OK);

        (await gate.GetAsync($"/api/organizer/organizations/{organizationId}/events")).ShouldFail(HttpStatusCode.Forbidden, "forbidden");
        (await gate.PostAsync($"/api/organizer/organizations/{organizationId}/events", new { title = "Kapıdan", description = "Açıklama metni yeterli.", category = "Other" }))
            .ShouldFail(HttpStatusCode.Forbidden, "forbidden");
    }
}

public sealed class CatalogValidationTests(PostgresFixture postgres) : IntegrationTest(postgres)
{
    [Theory]
    [InlineData("Turkey Standard Time")]
    [InlineData("Europe/Atlantis")]
    [InlineData("GMT+3")]
    [InlineData("")]
    public async Task Venue_rejects_non_iana_timezones(string timezone)
    {
        var (organizer, _, organizationId) = await OrganizerAsync("org-a@loca.test", "Kumru Sahne");
        (await organizer.PostAsync($"/api/organizer/organizations/{organizationId}/venues", new { name = "Salon", city = "İstanbul", address = "Adres 1", timezone }))
            .ShouldFail(HttpStatusCode.BadRequest, "invalid_timezone");
    }

    [Fact]
    public async Task Session_rejects_nonexistent_local_time_past_start_and_inverted_sales_window()
    {
        var (organizer, _, organizationId) = await OrganizerAsync("org-a@loca.test", "Kumru Sahne");
        var berlin = await CatalogSetup.VenueAsync(organizer, organizationId, "Berlin", "Europe/Berlin");
        var istanbul = await CatalogSetup.VenueAsync(organizer, organizationId);
        var (eventId, _) = await CatalogSetup.EventAsync(organizer, organizationId);
        var url = $"/api/organizer/events/{eventId}/sessions";

        // 2027-03-28 02:30 Europe/Berlin'de yoktur (saat 02:00'den 03:00'e atlar).
        (await organizer.PostAsync(url, new { venueId = berlin, startsAtLocal = "2027-03-28T02:30", salesMode = "GeneralAdmission" }))
            .ShouldFail(HttpStatusCode.BadRequest, "invalid_local_time");
        // 2027-10-31 02:30 iki kez yaşanır.
        (await organizer.PostAsync(url, new { venueId = berlin, startsAtLocal = "2027-10-31T02:30", salesMode = "GeneralAdmission" }))
            .ShouldFail(HttpStatusCode.BadRequest, "invalid_local_time");
        (await organizer.PostAsync(url, new { venueId = istanbul, startsAtLocal = "2026-11-20T20:30+03:00", salesMode = "GeneralAdmission" }))
            .ShouldFail(HttpStatusCode.BadRequest, "validation_failed");
        (await organizer.PostAsync(url, new { venueId = istanbul, startsAtLocal = CatalogSetup.Local(Clock.Now.AddMinutes(-1)), salesMode = "GeneralAdmission" }))
            .ShouldFail(HttpStatusCode.BadRequest, "validation_failed");
        (await organizer.PostAsync(url, new
        {
            venueId = istanbul, startsAtLocal = CatalogSetup.Local(Clock.Now.AddDays(5)),
            salesEndAtLocal = CatalogSetup.Local(Clock.Now.AddDays(6)), salesMode = "GeneralAdmission",
        })).ShouldFail(HttpStatusCode.BadRequest, "validation_failed");
        (await organizer.PostAsync(url, new { venueId = istanbul, startsAtLocal = CatalogSetup.Local(Clock.Now.AddDays(5)), salesMode = "Mixed" }))
            .ShouldBe(HttpStatusCode.BadRequest);
        (await organizer.PostAsync(url, new { venueId = istanbul, startsAtLocal = CatalogSetup.Local(Clock.Now.AddDays(5)), salesMode = "GeneralAdmission", holdTtlSeconds = 5 }))
            .ShouldFail(HttpStatusCode.BadRequest, "validation_failed");

        Assert.Equal(0, await Database.ScalarAsAdminAsync<long>("SELECT count(*) FROM catalog.sessions"));
    }

    [Fact]
    public async Task Prices_capacity_and_currency_are_validated()
    {
        var (organizer, _, organizationId) = await OrganizerAsync("org-a@loca.test", "Kumru Sahne");
        var venueId = await CatalogSetup.VenueAsync(organizer, organizationId);
        var layoutId = await CatalogSetup.LayoutAsync(organizer, venueId, twoTiers: true);
        var (eventId, _) = await CatalogSetup.EventAsync(organizer, organizationId);
        var seated = await CatalogSetup.DraftSessionAsync(organizer, eventId, venueId, Clock.Now.AddDays(5), "ReservedSeating");
        var ga = await CatalogSetup.DraftSessionAsync(organizer, eventId, venueId, Clock.Now.AddDays(6), "GeneralAdmission");

        (await organizer.PutAsync($"/api/organizer/sessions/{seated}/seating", new { layoutId, tierPrices = CatalogSetup.Tiers(("A", -1), ("B", 100)) })).ShouldFail(HttpStatusCode.BadRequest, "validation_failed");
        (await organizer.PutAsync($"/api/organizer/sessions/{seated}/seating", new { layoutId, tierPrices = CatalogSetup.Tiers(("A", 100)) })).ShouldFail(HttpStatusCode.BadRequest, "validation_failed");
        (await organizer.PutAsync($"/api/organizer/sessions/{seated}/seating", new { layoutId, tierPrices = CatalogSetup.Tiers(("A", 100), ("B", 100), ("C", 100)) })).ShouldFail(HttpStatusCode.BadRequest, "validation_failed");
        (await organizer.PutAsync($"/api/organizer/sessions/{seated}/seating", """{"layoutId":"LAYOUT","tierPrices":{"A":10.5,"B":100}}""".Replace("LAYOUT", layoutId.ToString())))
            .ShouldBe(HttpStatusCode.BadRequest); // kesirli tutar: para minor unit tam sayıdır
        (await organizer.PutAsync($"/api/organizer/sessions/{seated}/seating", new { layoutId, tierPrices = CatalogSetup.Tiers(("A", 100), ("B", 100)), currency = "USD" }))
            .ShouldFail(HttpStatusCode.BadRequest, "unsupported_currency");

        (await organizer.PostAsync($"/api/organizer/sessions/{ga}/categories", new { name = "Genel", priceMinor = -5, capacity = 10 })).ShouldFail(HttpStatusCode.BadRequest, "validation_failed");
        (await organizer.PostAsync($"/api/organizer/sessions/{ga}/categories", new { name = "Genel", priceMinor = 100, capacity = 0 })).ShouldFail(HttpStatusCode.BadRequest, "validation_failed");
        (await organizer.PostAsync($"/api/organizer/sessions/{ga}/categories", new { name = "Genel", priceMinor = 100, capacity = 10, currency = "EUR" })).ShouldFail(HttpStatusCode.BadRequest, "unsupported_currency");

        // Seans tek satış modu kullanır.
        (await organizer.PostAsync($"/api/organizer/sessions/{seated}/categories", new { name = "Genel", priceMinor = 100, capacity = 10 })).ShouldFail(HttpStatusCode.UnprocessableEntity, "invalid_selection");
        (await organizer.PutAsync($"/api/organizer/sessions/{ga}/seating", new { layoutId, tierPrices = CatalogSetup.Tiers(("A", 100), ("B", 100)) })).ShouldFail(HttpStatusCode.UnprocessableEntity, "invalid_selection");

        Assert.Equal(0, await Database.ScalarAsAdminAsync<long>("SELECT count(*) FROM inventory.session_seats"));
        Assert.Equal(0, await Database.ScalarAsAdminAsync<long>("SELECT count(*) FROM inventory.ga_categories"));
    }

    [Fact]
    public async Task Publish_guards_explain_what_is_missing()
    {
        var (organizer, _, organizationId) = await OrganizerAsync("org-a@loca.test", "Kumru Sahne", approve: false);
        var venueId = await CatalogSetup.VenueAsync(organizer, organizationId);
        var (eventId, _) = await CatalogSetup.EventAsync(organizer, organizationId, publish: false);
        var sessionId = await CatalogSetup.DraftSessionAsync(organizer, eventId, venueId, Clock.Now.AddDays(5), "GeneralAdmission");

        (await organizer.PostAsync($"/api/organizer/events/{eventId}/publish")).ShouldFail(HttpStatusCode.Conflict, "organization_not_active");
        var guard = (await organizer.PostAsync($"/api/organizer/sessions/{sessionId}/publish")).ShouldFail(HttpStatusCode.Conflict, "publish_guard_failed");
        var reasons = guard.Json["reasons"]!.AsArray().Select(r => r!.GetValue<string>()).ToList();
        Assert.Equal(["organization_not_active", "event_not_published", "no_ticket_category"], reasons);

        await Database.ExecuteAsAdminAsync("UPDATE identity.organizations SET status = 'Active' WHERE id = @id", new NpgsqlParameter("id", organizationId));
        (await organizer.PostAsync($"/api/organizer/events/{eventId}/publish")).ShouldBe(HttpStatusCode.OK);
        (await organizer.PostAsync($"/api/organizer/sessions/{sessionId}/categories", new { name = "Genel", priceMinor = 100, capacity = 10 })).ShouldBe(HttpStatusCode.OK);
        (await organizer.PostAsync($"/api/organizer/sessions/{sessionId}/publish")).ShouldBe(HttpStatusCode.OK);
        (await organizer.PostAsync($"/api/organizer/sessions/{sessionId}/publish")).ShouldFail(HttpStatusCode.Conflict, "invalid_state_transition");
        (await organizer.PutAsync($"/api/organizer/sessions/{sessionId}", new { venueId, startsAtLocal = CatalogSetup.Local(Clock.Now.AddDays(7)) }))
            .ShouldFail(HttpStatusCode.Conflict, "session_not_editable");
        (await organizer.PostAsync($"/api/organizer/events/{eventId}/unpublish")).ShouldFail(HttpStatusCode.Conflict, "publish_guard_failed");
    }
}

public sealed class LayoutAndInventoryGuardTests(PostgresFixture postgres) : IntegrationTest(postgres)
{
    [Fact]
    public async Task Layouts_are_immutable_versions_and_the_runtime_role_cannot_rewrite_them()
    {
        var (organizer, _, organizationId) = await OrganizerAsync("org-a@loca.test", "Kumru Sahne");
        var venueId = await CatalogSetup.VenueAsync(organizer, organizationId);
        var first = await CatalogSetup.LayoutAsync(organizer, venueId, rows: 2, seatsPerRow: 5);
        var second = await CatalogSetup.LayoutAsync(organizer, venueId, rows: 3, seatsPerRow: 4);

        var layouts = (await organizer.GetAsync($"/api/organizer/venues/{venueId}/layouts")).ShouldBe(HttpStatusCode.OK).Json.AsArray();
        Assert.Equal([2, 1], layouts.Select(l => l!["version"]!.GetValue<int>()));
        Assert.Equal(12, layouts[0]!["seatCount"]!.GetValue<int>());
        Assert.Equal(10, await Database.ScalarAsAdminAsync<long>("SELECT count(*) FROM catalog.layout_seats WHERE layout_id = @id", new NpgsqlParameter("id", first)));
        Assert.NotEqual(first, second);

        foreach (var sql in new[]
        {
            "UPDATE catalog.layout_seats SET tier_code = 'Z'", "DELETE FROM catalog.layout_seats",
            "UPDATE catalog.seat_layouts SET name = 'x'", "DELETE FROM catalog.seat_layouts",
        })
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => Database.ExecuteAsAppAsync(sql));
            Assert.Equal("42501", error.SqlState);
        }
    }

    [Fact]
    public async Task Concurrent_layout_creation_assigns_distinct_versions()
    {
        var (organizer, _, organizationId) = await OrganizerAsync("org-a@loca.test", "Kumru Sahne");
        var venueId = await CatalogSetup.VenueAsync(organizer, organizationId);

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => organizer.PostAsync($"/api/organizer/venues/{venueId}/layouts",
            new { name = "Eşzamanlı", sections = new[] { new { name = "S", tierCode = "A", rows = 1, seatsPerRow = 2 } } })));

        Assert.All(responses, r => r.ShouldBe(HttpStatusCode.Created));
        Assert.Equal(Enumerable.Range(1, 8), responses.Select(r => r.Json["version"]!.GetValue<int>()).Order());
    }

    [Fact]
    public async Task Seat_snapshot_survives_new_layout_versions_and_seating_is_locked_after_sales_begin()
    {
        var (organizer, _, organizationId) = await OrganizerAsync("org-a@loca.test", "Kumru Sahne");
        var setup = await CatalogSetup.SeatedSessionAsync(organizer, organizationId, Clock.Now.AddDays(10));
        var newLayout = await CatalogSetup.LayoutAsync(organizer, setup.VenueId, rows: 1, seatsPerRow: 3);

        // Yeni plan sürümü mevcut seansın koltuklarını değiştirmez.
        Assert.Equal(10, await Database.ScalarAsAdminAsync<long>("SELECT count(*) FROM inventory.session_seats WHERE session_id = @s", new NpgsqlParameter("s", setup.SessionId)));

        // Satış öncesi plan değiştirilebilir.
        (await organizer.PutAsync($"/api/organizer/sessions/{setup.SessionId}/seating", new { layoutId = newLayout, tierPrices = CatalogSetup.Tiers(("A", 20000)) })).ShouldBe(HttpStatusCode.OK);
        Assert.Equal(3, await Database.ScalarAsAdminAsync<long>("SELECT count(*) FROM inventory.session_seats WHERE session_id = @s", new NpgsqlParameter("s", setup.SessionId)));

        await InventoryTestData.MarkOneSeatSoldAsync(Database, setup.SessionId);

        (await organizer.PutAsync($"/api/organizer/sessions/{setup.SessionId}/seating", new { layoutId = setup.LayoutId, tierPrices = CatalogSetup.Tiers(("A", 45000)) }))
            .ShouldFail(HttpStatusCode.Conflict, "layout_locked");
        var locked = (await organizer.PutAsync($"/api/organizer/sessions/{setup.SessionId}/tier-prices", new { tierPrices = CatalogSetup.Tiers(("A", 99000)) }))
            .ShouldFail(HttpStatusCode.Conflict, "price_locked");
        Assert.Equal(["A"], locked.Json["tiers"]!.AsArray().Select(t => t!.GetValue<string>()));
        Assert.Equal(3, await Database.ScalarAsAdminAsync<long>("SELECT count(*) FROM inventory.session_seats WHERE session_id = @s AND price_minor = 20000", new NpgsqlParameter("s", setup.SessionId)));
    }

    [Fact]
    public async Task Tier_without_held_or_sold_seats_can_be_repriced_while_a_committed_tier_cannot()
    {
        var (organizer, _, organizationId) = await OrganizerAsync("org-a@loca.test", "Kumru Sahne");
        var setup = await CatalogSetup.SeatedSessionAsync(organizer, organizationId, Clock.Now.AddDays(10), twoTiers: true);
        await InventoryTestData.MarkOneSeatSoldAsync(Database, setup.SessionId, tier: "A");

        (await organizer.PutAsync($"/api/organizer/sessions/{setup.SessionId}/tier-prices", new { tierPrices = CatalogSetup.Tiers(("B", 12300)) })).ShouldBe(HttpStatusCode.OK);
        (await organizer.PutAsync($"/api/organizer/sessions/{setup.SessionId}/tier-prices", new { tierPrices = CatalogSetup.Tiers(("A", 1), ("B", 1)) })).ShouldFail(HttpStatusCode.Conflict, "price_locked");
        (await organizer.PutAsync($"/api/organizer/sessions/{setup.SessionId}/tier-prices", new { tierPrices = CatalogSetup.Tiers(("Z", 1)) })).ShouldFail(HttpStatusCode.UnprocessableEntity, "invalid_selection");

        Assert.Equal(12300, await Database.ScalarAsAdminAsync<long>("SELECT min(price_minor) FROM inventory.session_seats WHERE session_id = @s AND tier_code = 'B'", new NpgsqlParameter("s", setup.SessionId)));
        Assert.Equal(45000, await Database.ScalarAsAdminAsync<long>("SELECT min(price_minor) FROM inventory.session_seats WHERE session_id = @s AND tier_code = 'A'", new NpgsqlParameter("s", setup.SessionId)));
    }

    [Fact]
    public async Task Capacity_cannot_drop_below_held_plus_sold_in_the_api_or_in_the_database()
    {
        var (organizer, _, organizationId) = await OrganizerAsync("org-a@loca.test", "Kumru Sahne");
        var setup = await CatalogSetup.GaSessionAsync(organizer, organizationId, Clock.Now.AddDays(10), [("Genel Giriş", 25000, 10)]);
        var categoryId = setup.CategoryIds[0];
        await Database.ExecuteAsAdminAsync("UPDATE inventory.ga_categories SET sold_count = 4, held_count = 3 WHERE id = @id", new NpgsqlParameter("id", categoryId));

        var rejected = (await organizer.PutAsync($"/api/organizer/sessions/{setup.SessionId}/categories/{categoryId}", new { name = "Genel Giriş", priceMinor = 25000, capacity = 6 }))
            .ShouldFail(HttpStatusCode.Conflict, "capacity_below_committed");
        Assert.Equal(7, rejected.Json["committed"]!.GetValue<int>());
        (await organizer.PutAsync($"/api/organizer/sessions/{setup.SessionId}/categories/{categoryId}", new { name = "Genel Giriş", priceMinor = 26000, capacity = 7 })).ShouldBe(HttpStatusCode.OK);

        // Uygulama atlansa bile CHECK kısıtı ihlali reddeder.
        foreach (var sql in new[]
        {
            $"UPDATE inventory.ga_categories SET capacity = 6 WHERE id = '{categoryId}'",
            $"UPDATE inventory.ga_categories SET sold_count = 5 WHERE id = '{categoryId}'",
            $"UPDATE inventory.ga_categories SET held_count = -1 WHERE id = '{categoryId}'",
        })
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => Database.ExecuteAsAppAsync(sql));
            Assert.Equal("23514", error.SqlState);
        }
    }

    [Fact]
    public async Task Seat_ownership_check_constraint_rejects_inconsistent_rows()
    {
        var (organizer, _, organizationId) = await OrganizerAsync("org-a@loca.test", "Kumru Sahne");
        var setup = await CatalogSetup.SeatedSessionAsync(organizer, organizationId, Clock.Now.AddDays(10));
        foreach (var sql in new[]
        {
            $"UPDATE inventory.session_seats SET status = 'Held' WHERE session_id = '{setup.SessionId}'",
            $"UPDATE inventory.session_seats SET status = 'Sold' WHERE session_id = '{setup.SessionId}'",
            $"UPDATE inventory.session_seats SET held_until = now() WHERE session_id = '{setup.SessionId}'",
            $"UPDATE inventory.session_seats SET status = 'Reserved' WHERE session_id = '{setup.SessionId}'",
            $"UPDATE inventory.session_seats SET price_minor = -1 WHERE session_id = '{setup.SessionId}'",
        })
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => Database.ExecuteAsAppAsync(sql));
            Assert.Equal("23514", error.SqlState);
        }
    }
}

public sealed class CatalogPaginationTests(PostgresFixture postgres) : IntegrationTest(postgres)
{
    private async Task<List<(Guid Id, DateTimeOffset StartsAt)>> WalkAsync(string query, int limit)
    {
        var client = Factory.NewClient();
        var seen = new List<(Guid, DateTimeOffset)>();
        string? cursor = null;
        for (var guard = 0; guard < 50; guard++)
        {
            var url = $"/api/catalog/sessions?limit={limit}{query}{(cursor is null ? "" : "&cursor=" + cursor)}";
            var page = (await client.GetAsync(url)).ShouldBe(HttpStatusCode.OK);
            var items = page.Json["items"]!.AsArray();
            Assert.True(items.Count <= limit);
            seen.AddRange(items.Select(i => (i!["sessionId"]!.GetValue<Guid>(), i["startsAt"]!.GetValue<DateTimeOffset>())));
            cursor = page.Json["nextCursor"]?.GetValue<string>();
            if (cursor is null) return seen;
        }
        throw new InvalidOperationException("Pagination did not terminate.");
    }

    [Fact]
    public async Task Keyset_pagination_is_deterministic_complete_and_stable_under_inserts()
    {
        var (organizer, _, organizationId) = await OrganizerAsync("org-a@loca.test", "Kumru Sahne");
        var venueId = await CatalogSetup.VenueAsync(organizer, organizationId);
        var (eventId, _) = await CatalogSetup.EventAsync(organizer, organizationId);

        async Task<Guid> Add(DateTimeOffset startsAt)
        {
            var id = await CatalogSetup.DraftSessionAsync(organizer, eventId, venueId, startsAt, "GeneralAdmission");
            (await organizer.PostAsync($"/api/organizer/sessions/{id}/categories", new { name = "Genel", priceMinor = 10000, capacity = 5 })).ShouldBe(HttpStatusCode.OK);
            (await organizer.PostAsync($"/api/organizer/sessions/{id}/publish")).ShouldBe(HttpStatusCode.OK);
            return id;
        }

        var sameInstant = Clock.Now.AddDays(3);
        var created = new List<Guid>();
        foreach (var startsAt in new[] { Clock.Now.AddDays(9), sameInstant, Clock.Now.AddDays(1), sameInstant, Clock.Now.AddDays(5), sameInstant, Clock.Now.AddDays(7) })
            created.Add(await Add(startsAt));

        var walk = await WalkAsync("", limit: 3);
        Assert.Equal(7, walk.Count);
        Assert.Equal(created.Order(), walk.Select(w => w.Id).Order());
        // Sıra (startsAt, id): aynı ana düşen üç seans kimliğe göre sıralanır.
        Assert.Equal(walk.OrderBy(w => w.StartsAt).ThenBy(w => w.Id).ToList(), walk);
        Assert.Equal(walk, await WalkAsync("", limit: 3));
        Assert.Equal(walk, await WalkAsync("", limit: 2));
        Assert.Equal(walk, await WalkAsync("", limit: 50));

        // İlk sayfadan sonra araya (daha erken başlayan) kayıt girse de kalan sayfalar kayıt atlamaz/tekrarlamaz.
        var client = Factory.NewClient();
        var firstPage = (await client.GetAsync("/api/catalog/sessions?limit=3")).ShouldBe(HttpStatusCode.OK);
        var cursor = firstPage.Json["nextCursor"]!.GetValue<string>();
        await Add(Clock.Now.AddHours(12));
        var rest = new List<Guid>();
        while (cursor is not null)
        {
            var page = (await client.GetAsync($"/api/catalog/sessions?limit=3&cursor={cursor}")).ShouldBe(HttpStatusCode.OK);
            rest.AddRange(page.Json["items"]!.AsArray().Select(i => i!["sessionId"]!.GetValue<Guid>()));
            cursor = page.Json["nextCursor"]?.GetValue<string>();
        }
        Assert.Equal(walk.Skip(3).Select(w => w.Id), rest);
    }

    [Fact]
    public async Task Filters_by_city_category_and_date_range()
    {
        var (organizer, _, organizationId) = await OrganizerAsync("org-a@loca.test", "Kumru Sahne");
        var istanbulConcert = await CatalogSetup.SeatedSessionAsync(organizer, organizationId, Clock.Now.AddDays(3), city: "İstanbul", category: "Concert", title: "Konser A");
        var izmirTheatre = await CatalogSetup.SeatedSessionAsync(organizer, organizationId, Clock.Now.AddDays(6), city: "İzmir", category: "Theatre", title: "Tiyatro B");
        var istanbulFestival = await CatalogSetup.GaSessionAsync(organizer, organizationId, Clock.Now.AddDays(9), city: "İstanbul", category: "Festival", title: "Festival C");

        async Task<List<Guid>> Ids(string query) => [.. (await WalkAsync(query, 10)).Select(w => w.Id)];

        Assert.Equal([istanbulConcert.SessionId, istanbulFestival.SessionId], await Ids("&city=" + Uri.EscapeDataString("İstanbul")));
        Assert.Equal([izmirTheatre.SessionId], await Ids("&category=Theatre"));
        Assert.Equal([istanbulFestival.SessionId], await Ids("&city=" + Uri.EscapeDataString("İstanbul") + "&category=Festival"));
        var from = Uri.EscapeDataString(Clock.Now.AddDays(4).ToString("o"));
        var to = Uri.EscapeDataString(Clock.Now.AddDays(8).ToString("o"));
        Assert.Equal([izmirTheatre.SessionId], await Ids($"&from={from}&to={to}"));
        Assert.Empty(await Ids("&city=Ankara"));

        var filters = (await Factory.NewClient().GetAsync("/api/catalog/filters")).ShouldBe(HttpStatusCode.OK);
        Assert.Equal(["İstanbul", "İzmir"], filters.Json["cities"]!.AsArray().Select(c => c!.GetValue<string>()).Order(StringComparer.Ordinal));
        Assert.Equal(["Concert", "Theatre", "Festival"], filters.Json["categories"]!.AsArray().Select(c => c!.GetValue<string>()));

        var list = (await Factory.NewClient().GetAsync("/api/catalog/sessions")).ShouldBe(HttpStatusCode.OK);
        var first = list.Json["items"]![0]!;
        Assert.Equal(45000, first["minPriceMinor"]!.GetValue<long>());
        Assert.False(first["soldOut"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("?limit=0")]
    [InlineData("?limit=51")]
    [InlineData("?cursor=not-a-cursor")]
    [InlineData("?cursor=AAAA")]
    [InlineData("?category=Opera")]
    [InlineData("?from=2026-12-01T00:00:00Z&to=2026-11-01T00:00:00Z")]
    public async Task Invalid_query_is_rejected(string query) =>
        (await Factory.NewClient().GetAsync("/api/catalog/sessions" + query)).ShouldBe(HttpStatusCode.BadRequest);
}

public sealed class DemoSeedTests(PostgresFixture postgres) : IntegrationTest(postgres)
{
    private readonly string _passwordDirectory = Directory.CreateTempSubdirectory("loca-seed-test-").FullName;

    protected override IDictionary<string, string?> Settings => new Dictionary<string, string?>
    {
        ["Demo:SeedEnabled"] = "true",
        ["Demo:SeedPasswordDirectory"] = _passwordDirectory,
    };

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        Directory.Delete(_passwordDirectory, recursive: true);
    }

    [Fact]
    public async Task Seed_is_idempotent_labelled_as_demo_and_maps_users_to_password_files()
    {
        foreach (var user in DemoSeeder.Users)
            await File.WriteAllTextAsync(Path.Combine(_passwordDirectory, DemoSeeder.PasswordFileName(user.Email)), $"Seed-{user.Email.Length}-parola-2026");

        var first = await Factory.WithScopeAsync(sp => sp.GetRequiredService<DemoSeeder>().RunAsync(CancellationToken.None));
        var counts = await SnapshotAsync();
        var second = await Factory.WithScopeAsync(sp => sp.GetRequiredService<DemoSeeder>().RunAsync(CancellationToken.None));

        Assert.True(first.Created > 0);
        Assert.Equal(0, second.Created);
        Assert.Equal(counts, await SnapshotAsync());

        var page = (await Factory.NewClient().GetAsync("/api/catalog/sessions")).ShouldBe(HttpStatusCode.OK);
        var items = page.Json["items"]!.AsArray();
        Assert.Equal(4, items.Count);
        Assert.All(items, i =>
        {
            Assert.True(i!["isDemo"]!.GetValue<bool>());
            Assert.Contains("(Demo)", i["eventTitle"]!.GetValue<string>());
        });

        var admin = Factory.NewClient();
        var login = (await admin.LoginAsync(DemoSeeder.PlatformAdmin.Email, $"Seed-{DemoSeeder.PlatformAdmin.Email.Length}-parola-2026")).ShouldBe(HttpStatusCode.OK);
        Assert.True(login.Json["user"]!["isPlatformAdmin"]!.GetValue<bool>());
        Assert.Equal(1, await Database.ScalarAsAdminAsync<long>("SELECT count(*) FROM identity.users WHERE is_platform_admin"));
    }

    [Fact]
    public async Task Seed_fails_without_password_files_instead_of_inventing_default_credentials()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Factory.WithScopeAsync(sp => sp.GetRequiredService<DemoSeeder>().RunAsync(CancellationToken.None)));
        Assert.Equal(0, await Database.ScalarAsAdminAsync<long>("SELECT count(*) FROM identity.users"));
    }

    private async Task<string> SnapshotAsync() => (await Database.ScalarAsAdminAsync<string>(
        """
        SELECT concat_ws(',', (SELECT count(*) FROM identity.users), (SELECT count(*) FROM identity.organizations),
            (SELECT count(*) FROM identity.organization_members), (SELECT count(*) FROM catalog.venues), (SELECT count(*) FROM catalog.events),
            (SELECT count(*) FROM catalog.sessions), (SELECT count(*) FROM inventory.session_seats), (SELECT count(*) FROM inventory.ga_categories))
        """))!;
}
