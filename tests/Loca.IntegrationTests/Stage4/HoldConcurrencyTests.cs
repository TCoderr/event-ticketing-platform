using System.Net;
using Loca.IntegrationTests.Infrastructure;
using Npgsql;

namespace Loca.IntegrationTests.Stage4;

/// <summary>
/// Yarış testleri: tüm istekler aynı anda başlatılır ve sonuç her çalıştırmada kesin olmalıdır
/// (tek kazanan / kapasite kadar kazanan). Doğruluk, bekleme süresine değil veritabanı kilit ve kısıtlarına dayanır.
/// </summary>
public sealed class SeatHoldConcurrencyTests(PostgresFixture postgres) : IntegrationTest(postgres)
{
    private NpgsqlParameter Session(Guid id) => new("s", id);

    [Fact]
    public async Task Twenty_five_concurrent_requests_for_the_same_seat_produce_exactly_one_hold()
    {
        var (organizer, _, organizationId) = await OrganizerAsync("org-a@loca.test", "Kumru Sahne");
        var setup = await CatalogSetup.SeatedSessionAsync(organizer, organizationId, Clock.Now.AddDays(10));
        var customers = await CustomersAsync(25);
        var seat = setup.SeatIds[0];

        var responses = await Task.WhenAll(customers.Select(c => Task.Run(() => c.HoldSeatsAsync(setup.SessionId, [seat]))));

        Assert.Single(responses, r => r.Status == HttpStatusCode.Created);
        Assert.Equal(24, responses.Count(r => r.Status == HttpStatusCode.Conflict && r.Code == "seats_unavailable"));
        Assert.Equal(1, await Database.ScalarAsAdminAsync<long>("SELECT count(*) FROM inventory.reservations WHERE session_id = @s", Session(setup.SessionId)));
        Assert.Equal(1, await Database.ScalarAsAdminAsync<long>("SELECT count(*) FROM inventory.reservation_lines"));
        Assert.Equal(1, await Database.ScalarAsAdminAsync<long>("SELECT count(*) FROM inventory.session_seats WHERE session_id = @s AND status = 'Held'", Session(setup.SessionId)));

        var winner = responses.Single(r => r.Status == HttpStatusCode.Created).HoldId();
        Assert.Equal(winner, await Database.ScalarAsAdminAsync<Guid>("SELECT hold_reservation_id FROM inventory.session_seats WHERE id = @id", new NpgsqlParameter("id", seat)));
    }

    [Fact]
    public async Task Overlapping_multi_seat_requests_in_opposite_orders_are_all_or_nothing_and_never_split_the_seats()
    {
        var (organizer, _, organizationId) = await OrganizerAsync("org-a@loca.test", "Kumru Sahne");
        var setup = await CatalogSetup.SeatedSessionAsync(organizer, organizationId, Clock.Now.AddDays(10));
        var customers = await CustomersAsync(20);
        Guid[] forward = [setup.SeatIds[0], setup.SeatIds[1], setup.SeatIds[2]];
        Guid[] backward = [.. forward.Reverse()];

        var responses = await Task.WhenAll(customers.Select((c, i) => Task.Run(() => c.HoldSeatsAsync(setup.SessionId, i % 2 == 0 ? forward : backward))));

        Assert.Single(responses, r => r.Status == HttpStatusCode.Created);
        Assert.All(responses.Where(r => r.Status != HttpStatusCode.Created), r => r.ShouldFail(HttpStatusCode.Conflict, "seats_unavailable"));
        Assert.Equal(1, await Database.ScalarAsAdminAsync<long>(
            "SELECT count(DISTINCT hold_reservation_id) FROM inventory.session_seats WHERE session_id = @s AND status = 'Held'", Session(setup.SessionId)));
        Assert.Equal(3, await Database.ScalarAsAdminAsync<long>("SELECT count(*) FROM inventory.session_seats WHERE session_id = @s AND status = 'Held'", Session(setup.SessionId)));
        Assert.Equal(3, await Database.ScalarAsAdminAsync<long>("SELECT count(*) FROM inventory.reservation_lines"));
    }

    [Fact]
    public async Task Disjoint_seat_requests_all_succeed_concurrently()
    {
        var (organizer, _, organizationId) = await OrganizerAsync("org-a@loca.test", "Kumru Sahne");
        var setup = await CatalogSetup.SeatedSessionAsync(organizer, organizationId, Clock.Now.AddDays(10));
        var customers = await CustomersAsync(10);

        var responses = await Task.WhenAll(customers.Select((c, i) => Task.Run(() => c.HoldSeatsAsync(setup.SessionId, [setup.SeatIds[i]]))));

        Assert.All(responses, r => r.ShouldBe(HttpStatusCode.Created));
        Assert.Equal(10, await Database.ScalarAsAdminAsync<long>(
            "SELECT count(DISTINCT hold_reservation_id) FROM inventory.session_seats WHERE session_id = @s AND status = 'Held'", Session(setup.SessionId)));
    }

    [Fact]
    public async Task Same_user_submitting_two_different_holds_at_once_ends_with_exactly_one_active_hold()
    {
        var (organizer, _, organizationId) = await OrganizerAsync("org-a@loca.test", "Kumru Sahne");
        var setup = await CatalogSetup.SeatedSessionAsync(organizer, organizationId, Clock.Now.AddDays(10));
        var (customer, _) = await SignedInAsync("cift@loca.test");

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(() => customer.HoldSeatsAsync(setup.SessionId, [setup.SeatIds[i]]))));

        Assert.Single(responses, r => r.Status == HttpStatusCode.Created);
        Assert.All(responses.Where(r => r.Status != HttpStatusCode.Created), r => r.ShouldFail(HttpStatusCode.Conflict, "active_hold_exists"));
        Assert.Equal(1, await Database.ScalarAsAdminAsync<long>("SELECT count(*) FROM inventory.reservations WHERE status = 'Active'"));
        Assert.Equal(1, await Database.ScalarAsAdminAsync<long>("SELECT count(*) FROM inventory.session_seats WHERE session_id = @s AND status = 'Held'", Session(setup.SessionId)));
    }

    [Fact]
    public async Task Database_rejects_a_second_active_hold_for_the_same_user_and_session_even_without_the_application()
    {
        var (organizer, _, organizationId) = await OrganizerAsync("org-a@loca.test", "Kumru Sahne");
        var setup = await CatalogSetup.SeatedSessionAsync(organizer, organizationId, Clock.Now.AddDays(10));
        var (customer, userId) = await SignedInAsync("tek@loca.test");
        (await customer.HoldSeatsAsync(setup.SessionId, [setup.SeatIds[0]])).ShouldBe(HttpStatusCode.Created);

        var error = await Assert.ThrowsAsync<PostgresException>(() => Database.ExecuteAsAppAsync($"""
            INSERT INTO inventory.reservations (id, session_id, user_id, status, created_at, expires_at, ticket_count, total_minor, currency)
            VALUES (gen_random_uuid(), '{setup.SessionId}', '{userId}', 'Active', now(), now() + interval '10 minutes', 1, 0, 'TRY')
            """));
        Assert.Equal("23505", error.SqlState);
        Assert.Equal("ux_reservations_one_active_per_user_session", error.ConstraintName);
    }
}

public sealed class GeneralAdmissionConcurrencyTests(PostgresFixture postgres) : IntegrationTest(postgres)
{
    private async Task<(int Held, int Sold, int Capacity)> CountsAsync(Guid categoryId)
    {
        var text = await Database.ScalarAsAdminAsync<string>(
            "SELECT held_count || ',' || sold_count || ',' || capacity FROM inventory.ga_categories WHERE id = @id", new NpgsqlParameter("id", categoryId));
        var parts = text!.Split(',').Select(int.Parse).ToArray();
        return (parts[0], parts[1], parts[2]);
    }

    [Fact]
    public async Task Thirty_single_ticket_requests_against_capacity_ten_sell_exactly_ten()
    {
        var (organizer, _, organizationId) = await OrganizerAsync("org-a@loca.test", "Kumru Sahne");
        var setup = await CatalogSetup.GaSessionAsync(organizer, organizationId, Clock.Now.AddDays(10), [("Genel Giriş", 25000, 10)]);
        var customers = await CustomersAsync(30);

        var responses = await Task.WhenAll(customers.Select(c => Task.Run(() => c.HoldGaAsync(setup.SessionId, null, (setup.CategoryIds[0], 1)))));

        Assert.Equal(10, responses.Count(r => r.Status == HttpStatusCode.Created));
        Assert.Equal(20, responses.Count(r => r.Status == HttpStatusCode.Conflict && r.Code == "insufficient_capacity"));
        Assert.Equal((10, 0, 10), await CountsAsync(setup.CategoryIds[0]));
        Assert.Equal(10, await Database.ScalarAsAdminAsync<long>("SELECT coalesce(sum(quantity), 0) FROM inventory.reservation_lines"));
    }

    [Fact]
    public async Task Requests_of_three_against_capacity_ten_never_exceed_capacity_and_fill_what_fits()
    {
        var (organizer, _, organizationId) = await OrganizerAsync("org-a@loca.test", "Kumru Sahne");
        var setup = await CatalogSetup.GaSessionAsync(organizer, organizationId, Clock.Now.AddDays(10), [("Genel Giriş", 25000, 10)]);
        var customers = await CustomersAsync(8);

        var responses = await Task.WhenAll(customers.Select(c => Task.Run(() => c.HoldGaAsync(setup.SessionId, null, (setup.CategoryIds[0], 3)))));

        Assert.Equal(3, responses.Count(r => r.Status == HttpStatusCode.Created));
        Assert.All(responses.Where(r => r.Status != HttpStatusCode.Created), r => r.ShouldFail(HttpStatusCode.Conflict, "insufficient_capacity"));
        Assert.Equal((9, 0, 10), await CountsAsync(setup.CategoryIds[0]));

        // Kalan tek bilet alınabilir; ikinci tek bilet artık yoktur.
        var extras = await CustomersAsync(2, "ek");
        (await extras[0].HoldGaAsync(setup.SessionId, null, (setup.CategoryIds[0], 1))).ShouldBe(HttpStatusCode.Created);
        var rejected = (await extras[1].HoldGaAsync(setup.SessionId, null, (setup.CategoryIds[0], 1))).ShouldFail(HttpStatusCode.Conflict, "insufficient_capacity");
        Assert.Equal(0, rejected.Json["categories"]![0]!["available"]!.GetValue<int>());
        Assert.Equal((10, 0, 10), await CountsAsync(setup.CategoryIds[0]));
    }

    [Fact]
    public async Task Demand_below_capacity_is_fully_served()
    {
        var (organizer, _, organizationId) = await OrganizerAsync("org-a@loca.test", "Kumru Sahne");
        var setup = await CatalogSetup.GaSessionAsync(organizer, organizationId, Clock.Now.AddDays(10), [("Genel Giriş", 25000, 20)]);
        var customers = await CustomersAsync(6);

        var responses = await Task.WhenAll(customers.Select(c => Task.Run(() => c.HoldGaAsync(setup.SessionId, null, (setup.CategoryIds[0], 2)))));

        Assert.All(responses, r => r.ShouldBe(HttpStatusCode.Created));
        Assert.Equal((12, 0, 20), await CountsAsync(setup.CategoryIds[0]));
    }

    [Fact]
    public async Task Multi_category_requests_leave_no_partial_claim_when_the_scarce_category_runs_out()
    {
        var (organizer, _, organizationId) = await OrganizerAsync("org-a@loca.test", "Kumru Sahne");
        var setup = await CatalogSetup.GaSessionAsync(organizer, organizationId, Clock.Now.AddDays(10), [("Genel Giriş", 25000, 50), ("Sahne Önü", 40000, 2)]);
        var customers = await CustomersAsync(12);

        // Yarısı kategorileri ters sırada gönderir: kilit sırası istek sırasına bağlı olmamalı.
        var responses = await Task.WhenAll(customers.Select((c, i) => Task.Run(() => i % 2 == 0
            ? c.HoldGaAsync(setup.SessionId, null, (setup.CategoryIds[0], 2), (setup.CategoryIds[1], 1))
            : c.HoldGaAsync(setup.SessionId, null, (setup.CategoryIds[1], 1), (setup.CategoryIds[0], 2)))));

        Assert.Equal(2, responses.Count(r => r.Status == HttpStatusCode.Created));
        Assert.All(responses.Where(r => r.Status != HttpStatusCode.Created), r => r.ShouldFail(HttpStatusCode.Conflict, "insufficient_capacity"));
        Assert.Equal((4, 0, 50), await CountsAsync(setup.CategoryIds[0])); // yalnız iki başarılı isteğin 2'şer bileti
        Assert.Equal((2, 0, 2), await CountsAsync(setup.CategoryIds[1]));
    }
}

public sealed class HoldAtomicityTests(PostgresFixture postgres) : IntegrationTest(postgres)
{
    [Fact]
    public async Task One_unavailable_seat_fails_the_whole_request_and_reserves_nothing()
    {
        var (organizer, _, organizationId) = await OrganizerAsync("org-a@loca.test", "Kumru Sahne");
        var setup = await CatalogSetup.SeatedSessionAsync(organizer, organizationId, Clock.Now.AddDays(10));
        var customers = await CustomersAsync(2);
        (await customers[0].HoldSeatsAsync(setup.SessionId, [setup.SeatIds[1]])).ShouldBe(HttpStatusCode.Created);

        var rejected = (await customers[1].HoldSeatsAsync(setup.SessionId, [setup.SeatIds[0], setup.SeatIds[1], setup.SeatIds[2]]))
            .ShouldFail(HttpStatusCode.Conflict, "seats_unavailable");

        Assert.Equal([setup.SeatIds[1]], rejected.Json["seatIds"]!.AsArray().Select(s => s!.GetValue<Guid>()));
        Assert.Equal(1, await Database.ScalarAsAdminAsync<long>("SELECT count(*) FROM inventory.session_seats WHERE status = 'Held'"));
        Assert.Equal(1, await Database.ScalarAsAdminAsync<long>("SELECT count(*) FROM inventory.reservations"));
        Assert.Equal(1, await Database.ScalarAsAdminAsync<long>("SELECT count(*) FROM ops.audit_log WHERE action = 'hold.created'"));
    }

    [Fact]
    public async Task Seat_of_another_session_or_unknown_seat_is_rejected_without_side_effects()
    {
        var (organizer, _, organizationId) = await OrganizerAsync("org-a@loca.test", "Kumru Sahne");
        var first = await CatalogSetup.SeatedSessionAsync(organizer, organizationId, Clock.Now.AddDays(10), title: "Birinci Seans");
        var second = await CatalogSetup.SeatedSessionAsync(organizer, organizationId, Clock.Now.AddDays(11), title: "İkinci Seans");
        var (customer, _) = await SignedInAsync("musteri@loca.test");

        (await customer.HoldSeatsAsync(first.SessionId, [first.SeatIds[0], second.SeatIds[0]])).ShouldFail(HttpStatusCode.UnprocessableEntity, "invalid_selection");
        (await customer.HoldSeatsAsync(first.SessionId, [first.SeatIds[0], Guid.NewGuid()])).ShouldFail(HttpStatusCode.UnprocessableEntity, "invalid_selection");

        Assert.Equal(0, await Database.ScalarAsAdminAsync<long>("SELECT count(*) FROM inventory.session_seats WHERE status <> 'Available'"));
        Assert.Equal(0, await Database.ScalarAsAdminAsync<long>("SELECT count(*) FROM inventory.reservations"));
    }

    [Fact]
    public async Task General_admission_request_that_fails_on_its_second_line_leaves_the_first_category_untouched()
    {
        var (organizer, _, organizationId) = await OrganizerAsync("org-a@loca.test", "Kumru Sahne");
        var setup = await CatalogSetup.GaSessionAsync(organizer, organizationId, Clock.Now.AddDays(10), [("Genel Giriş", 25000, 50), ("Sahne Önü", 40000, 3)]);
        var (customer, _) = await SignedInAsync("musteri@loca.test");

        var rejected = (await customer.HoldGaAsync(setup.SessionId, null, (setup.CategoryIds[0], 2), (setup.CategoryIds[1], 4)))
            .ShouldFail(HttpStatusCode.Conflict, "insufficient_capacity");

        var scarce = rejected.Json["categories"]!.AsArray().Single(c => c!["categoryId"]!.GetValue<Guid>() == setup.CategoryIds[1])!;
        Assert.Equal(3, scarce["available"]!.GetValue<int>());
        Assert.Equal(0, await Database.ScalarAsAdminAsync<long>("SELECT coalesce(sum(held_count), 0) FROM inventory.ga_categories"));
        Assert.Equal(0, await Database.ScalarAsAdminAsync<long>("SELECT count(*) FROM inventory.reservations"));
    }

    [Fact]
    public async Task Selection_rules_are_enforced()
    {
        var (organizer, _, organizationId) = await OrganizerAsync("org-a@loca.test", "Kumru Sahne");
        var seated = await CatalogSetup.SeatedSessionAsync(organizer, organizationId, Clock.Now.AddDays(10), title: "Koltuklu Seans");
        var ga = await CatalogSetup.GaSessionAsync(organizer, organizationId, Clock.Now.AddDays(11), [("Genel Giriş", 25000, 50), ("Sahne Önü", 40000, 50)]);
        var (customer, _) = await SignedInAsync("musteri@loca.test");
        var url = $"/api/sessions/{seated.SessionId}/holds";

        (await customer.HoldSeatsAsync(seated.SessionId, seated.SeatIds.Take(7))).ShouldFail(HttpStatusCode.UnprocessableEntity, "invalid_selection");
        (await customer.HoldSeatsAsync(seated.SessionId, [])).ShouldFail(HttpStatusCode.UnprocessableEntity, "invalid_selection");
        (await customer.HoldSeatsAsync(seated.SessionId, [seated.SeatIds[0], seated.SeatIds[0]])).ShouldFail(HttpStatusCode.UnprocessableEntity, "invalid_selection");
        (await customer.PostAsync(url, new { }, HoldApi.NewKey())).ShouldFail(HttpStatusCode.UnprocessableEntity, "invalid_selection");
        (await customer.PostAsync(url, new { seatIds = new[] { seated.SeatIds[0] }, items = new[] { new { categoryId = ga.CategoryIds[0], quantity = 1 } } }, HoldApi.NewKey()))
            .ShouldFail(HttpStatusCode.UnprocessableEntity, "invalid_selection");
        (await customer.PostAsync(url, """{"seatIds":[],"priceMinor":1}""", HoldApi.NewKey())).ShouldFail(HttpStatusCode.BadRequest, "unknown_field");

        // Seans tek satış modu kullanır.
        (await customer.HoldGaAsync(seated.SessionId, null, (ga.CategoryIds[0], 1))).ShouldFail(HttpStatusCode.UnprocessableEntity, "invalid_selection");
        (await customer.HoldSeatsAsync(ga.SessionId, [seated.SeatIds[0]])).ShouldFail(HttpStatusCode.UnprocessableEntity, "invalid_selection");

        (await customer.HoldGaAsync(ga.SessionId, null, (ga.CategoryIds[0], 0))).ShouldFail(HttpStatusCode.UnprocessableEntity, "invalid_selection");
        (await customer.HoldGaAsync(ga.SessionId, null, (ga.CategoryIds[0], 4), (ga.CategoryIds[1], 3))).ShouldFail(HttpStatusCode.UnprocessableEntity, "invalid_selection");
        (await customer.HoldGaAsync(ga.SessionId, null, (ga.CategoryIds[0], 1), (ga.CategoryIds[0], 1))).ShouldFail(HttpStatusCode.UnprocessableEntity, "invalid_selection");
        (await customer.HoldGaAsync(ga.SessionId, null, (Guid.NewGuid(), 1))).ShouldFail(HttpStatusCode.UnprocessableEntity, "invalid_selection");
        (await customer.HoldGaAsync(ga.SessionId, null, (ga.CategoryIds[0], 3), (ga.CategoryIds[1], 3))).ShouldBe(HttpStatusCode.Created); // tam 6

        Assert.Equal(1, await Database.ScalarAsAdminAsync<long>("SELECT count(*) FROM inventory.reservations"));
        (await Factory.NewClient().PostAsync(url, new { seatIds = new[] { seated.SeatIds[0] } }, HoldApi.NewKey(), csrf: false))
            .ShouldFail(HttpStatusCode.Unauthorized, "unauthenticated");
    }

    [Fact]
    public async Task Price_is_snapshotted_at_hold_time_and_the_snapshot_is_immutable()
    {
        var (organizer, _, organizationId) = await OrganizerAsync("org-a@loca.test", "Kumru Sahne");
        var setup = await CatalogSetup.GaSessionAsync(organizer, organizationId, Clock.Now.AddDays(10), [("Genel Giriş", 25000, 50)]);
        var (customer, _) = await SignedInAsync("musteri@loca.test");

        var hold = (await customer.HoldGaAsync(setup.SessionId, null, (setup.CategoryIds[0], 2))).ShouldBe(HttpStatusCode.Created);
        Assert.Equal(50000, hold.Json["totalMinor"]!.GetValue<long>());
        Assert.Equal("TRY", hold.Json["currency"]!.GetValue<string>());

        (await organizer.PutAsync($"/api/organizer/sessions/{setup.SessionId}/categories/{setup.CategoryIds[0]}", new { name = "Genel Giriş", priceMinor = 99000, capacity = 50 }))
            .ShouldBe(HttpStatusCode.OK);

        var reread = (await customer.GetAsync($"/api/holds/{hold.HoldId()}")).ShouldBe(HttpStatusCode.OK);
        Assert.Equal(50000, reread.Json["totalMinor"]!.GetValue<long>());
        Assert.Equal(25000, reread.Json["lines"]![0]!["unitPriceMinor"]!.GetValue<long>());

        foreach (var sql in new[] { "UPDATE inventory.reservation_lines SET unit_price_minor = 1", "DELETE FROM inventory.reservation_lines" })
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => Database.ExecuteAsAppAsync(sql));
            Assert.Equal("42501", error.SqlState);
        }
    }

    [Fact]
    public async Task Sales_window_and_visibility_gate_the_hold()
    {
        var (organizer, _, organizationId) = await OrganizerAsync("org-a@loca.test", "Kumru Sahne");
        var venueId = await CatalogSetup.VenueAsync(organizer, organizationId);
        var (eventId, _) = await CatalogSetup.EventAsync(organizer, organizationId);
        var sessionId = await CatalogSetup.DraftSessionAsync(organizer, eventId, venueId, Clock.Now.AddDays(10), "GeneralAdmission",
            salesStartAt: Clock.Now.AddDays(1), salesEndAt: Clock.Now.AddDays(5));
        var category = (await organizer.PostAsync($"/api/organizer/sessions/{sessionId}/categories", new { name = "Genel", priceMinor = 10000, capacity = 5 }))
            .ShouldBe(HttpStatusCode.OK).Json["categories"]![0]!["id"]!.GetValue<Guid>();
        var (customer, _) = await SignedInAsync("musteri@loca.test");

        (await customer.HoldGaAsync(sessionId, null, (category, 1))).ShouldFail(HttpStatusCode.NotFound, "not_found"); // taslak
        (await organizer.PostAsync($"/api/organizer/sessions/{sessionId}/publish")).ShouldBe(HttpStatusCode.OK);

        (await customer.HoldGaAsync(sessionId, null, (category, 1))).ShouldFail(HttpStatusCode.Conflict, "sales_not_started");
        Clock.Advance(TimeSpan.FromDays(1)); // now == salesStartAt → açık
        await customer.ReloginAsync("musteri@loca.test"); // oturum ömrü (12 saat) doldu
        var hold = (await customer.HoldGaAsync(sessionId, null, (category, 1))).ShouldBe(HttpStatusCode.Created);
        (await customer.DeleteAsync($"/api/holds/{hold.HoldId()}")).ShouldBe(HttpStatusCode.NoContent);

        Clock.Advance(TimeSpan.FromDays(4)); // now == salesEndAt → kapalı
        await customer.ReloginAsync("musteri@loca.test");
        (await customer.HoldGaAsync(sessionId, null, (category, 1))).ShouldFail(HttpStatusCode.Conflict, "sales_closed");
        Assert.Equal(0, await Database.ScalarAsAdminAsync<long>("SELECT held_count FROM inventory.ga_categories WHERE id = @id", new NpgsqlParameter("id", category)));
    }
}
