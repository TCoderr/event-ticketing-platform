using System.Net;
using Loca.Api.Infrastructure.Http;
using Loca.Api.Infrastructure.Tx;
using Loca.Api.Modules.Inventory;
using Loca.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Loca.IntegrationTests.Stage4;

public sealed class HoldExpiryTests(PostgresFixture postgres) : IntegrationTest(postgres)
{
    private Task<int> RunExpiryWorkerAsync() =>
        Factory.WithScopeAsync(sp => sp.GetRequiredService<HoldExpiryProcessor>().RunOnceAsync(CancellationToken.None));

    private Task<string?> StatusAsync(Guid holdId) =>
        Database.ScalarAsAdminAsync<string>("SELECT status FROM inventory.reservations WHERE id = @id", new NpgsqlParameter("id", holdId));

    [Fact]
    public async Task Hold_response_states_exactly_what_is_guaranteed_and_until_when()
    {
        var (organizer, _, organizationId) = await OrganizerAsync("org-a@loca.test", "Kumru Sahne");
        var setup = await CatalogSetup.SeatedSessionAsync(organizer, organizationId, Clock.Now.AddDays(10));
        var (customer, _) = await SignedInAsync("musteri@loca.test");

        var hold = (await customer.HoldSeatsAsync(setup.SessionId, [setup.SeatIds[0], setup.SeatIds[1]])).ShouldBe(HttpStatusCode.Created);

        Assert.Equal("Active", hold.Json["status"]!.GetValue<string>());
        Assert.Equal(Clock.Now, hold.Json["serverTime"]!.GetValue<DateTimeOffset>());
        Assert.Equal(Clock.Now.AddSeconds(600), hold.Json["expiresAt"]!.GetValue<DateTimeOffset>());
        Assert.Equal("inventory_reserved_until_expiry_not_a_purchase", hold.Json["guarantee"]!.GetValue<string>());
        Assert.Equal(90000, hold.Json["totalMinor"]!.GetValue<long>());
        Assert.Equal(2, hold.Json["ticketCount"]!.GetValue<int>());
        Assert.Null(hold.Json["orderId"]);
        Assert.Equal($"/api/holds/{hold.HoldId()}", hold.Header("Location"));
        Assert.Equal("no-store", hold.Header("Cache-Control"));

        var mine = (await customer.GetAsync($"/api/sessions/{setup.SessionId}/my-hold")).ShouldBe(HttpStatusCode.OK);
        Assert.Equal(hold.HoldId(), mine.Json["id"]!.GetValue<Guid>());
        var (other, _) = await SignedInAsync("baskasi@loca.test");
        (await other.GetAsync($"/api/holds/{hold.HoldId()}")).ShouldFail(HttpStatusCode.NotFound, "not_found");
        (await other.DeleteAsync($"/api/holds/{hold.HoldId()}")).ShouldFail(HttpStatusCode.NotFound, "not_found");
        (await other.GetAsync($"/api/sessions/{setup.SessionId}/my-hold")).ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Seat_hold_is_active_until_one_microsecond_before_expiry_and_can_be_taken_over_at_the_expiry_instant_without_a_worker()
    {
        var (organizer, _, organizationId) = await OrganizerAsync("org-a@loca.test", "Kumru Sahne");
        var setup = await CatalogSetup.SeatedSessionAsync(organizer, organizationId, Clock.Now.AddDays(10));
        var customers = await CustomersAsync(2);
        var seat = setup.SeatIds[0];
        var first = (await customers[0].HoldSeatsAsync(setup.SessionId, [seat])).HoldId();

        Clock.Advance(TimeSpan.FromSeconds(600) - TimeSpan.FromMicroseconds(1));
        (await customers[1].HoldSeatsAsync(setup.SessionId, [seat])).ShouldFail(HttpStatusCode.Conflict, "seats_unavailable");
        Assert.Equal("Active", (await customers[0].GetAsync($"/api/holds/{first}")).Json["status"]!.GetValue<string>());

        Clock.Advance(TimeSpan.FromMicroseconds(1)); // now == expiresAt: süre dolmuştur; worker hiç çalışmadı
        Assert.Equal("Expired", (await customers[0].GetAsync($"/api/holds/{first}")).Json["status"]!.GetValue<string>());
        (await customers[0].GetAsync($"/api/sessions/{setup.SessionId}/my-hold")).ShouldBe(HttpStatusCode.NoContent);
        (await customers[0].DeleteAsync($"/api/holds/{first}")).ShouldFail(HttpStatusCode.Conflict, "hold_not_active");
        var second = (await customers[1].HoldSeatsAsync(setup.SessionId, [seat])).HoldId();
        Assert.Equal(second, await Database.ScalarAsAdminAsync<Guid>("SELECT hold_reservation_id FROM inventory.session_seats WHERE id = @id", new NpgsqlParameter("id", seat)));

        // Geç kalan worker eski satırı kapatır ama artık başkasına ait olan koltuğa dokunmaz.
        Assert.Equal(1, await RunExpiryWorkerAsync());
        Assert.Equal("Expired", await StatusAsync(first));
        Assert.Equal("Active", await StatusAsync(second));
        Assert.Equal(second, await Database.ScalarAsAdminAsync<Guid>("SELECT hold_reservation_id FROM inventory.session_seats WHERE id = @id", new NpgsqlParameter("id", seat)));
        Assert.Equal("Held", await Database.ScalarAsAdminAsync<string>("SELECT status FROM inventory.session_seats WHERE id = @id", new NpgsqlParameter("id", seat)));
    }

    [Fact]
    public async Task Worker_expires_holds_frees_inventory_and_is_safe_to_run_again()
    {
        var (organizer, _, organizationId) = await OrganizerAsync("org-a@loca.test", "Kumru Sahne");
        var seated = await CatalogSetup.SeatedSessionAsync(organizer, organizationId, Clock.Now.AddDays(10), title: "Koltuklu Seans");
        var ga = await CatalogSetup.GaSessionAsync(organizer, organizationId, Clock.Now.AddDays(11), [("Genel Giriş", 25000, 5)]);
        var (customer, _) = await SignedInAsync("musteri@loca.test");
        var seatHold = (await customer.HoldSeatsAsync(seated.SessionId, [seated.SeatIds[0], seated.SeatIds[1]])).HoldId();
        var gaHold = (await customer.HoldGaAsync(ga.SessionId, null, (ga.CategoryIds[0], 3))).HoldId();

        Assert.Equal(0, await RunExpiryWorkerAsync()); // süre dolmadan dokunmaz
        Clock.Advance(TimeSpan.FromSeconds(600));
        Assert.Equal(2, await RunExpiryWorkerAsync());
        Assert.Equal(0, await RunExpiryWorkerAsync());

        Assert.Equal("Expired", await StatusAsync(seatHold));
        Assert.Equal("Expired", await StatusAsync(gaHold));
        Assert.Equal(0, await Database.ScalarAsAdminAsync<long>("SELECT count(*) FROM inventory.session_seats WHERE status <> 'Available' OR hold_reservation_id IS NOT NULL"));
        Assert.Equal(0, await Database.ScalarAsAdminAsync<long>("SELECT held_count FROM inventory.ga_categories WHERE id = @id", new NpgsqlParameter("id", ga.CategoryIds[0])));
        Assert.Equal(2, await Database.ScalarAsAdminAsync<long>("SELECT count(*) FROM ops.audit_log WHERE action = 'hold.expired' AND actor_kind = 'Worker'"));
        Assert.Equal(2, await Database.ScalarAsAdminAsync<long>("SELECT count(*) FROM inventory.reservations WHERE closed_at IS NOT NULL AND close_reason = 'Expired'"));
    }

    [Fact]
    public async Task General_admission_claim_reaps_expired_holds_itself_when_capacity_is_short()
    {
        var (organizer, _, organizationId) = await OrganizerAsync("org-a@loca.test", "Kumru Sahne");
        var setup = await CatalogSetup.GaSessionAsync(organizer, organizationId, Clock.Now.AddDays(10), [("Genel Giriş", 25000, 2)]);
        var customers = await CustomersAsync(2);
        var first = (await customers[0].HoldGaAsync(setup.SessionId, null, (setup.CategoryIds[0], 2))).HoldId();

        Clock.Advance(TimeSpan.FromSeconds(599));
        (await customers[1].HoldGaAsync(setup.SessionId, null, (setup.CategoryIds[0], 1))).ShouldFail(HttpStatusCode.Conflict, "insufficient_capacity");
        Clock.Advance(TimeSpan.FromSeconds(1));
        var second = (await customers[1].HoldGaAsync(setup.SessionId, null, (setup.CategoryIds[0], 2))).HoldId();

        Assert.Equal("Expired", await StatusAsync(first));
        Assert.Equal("Active", await StatusAsync(second));
        Assert.Equal(2, await Database.ScalarAsAdminAsync<long>("SELECT held_count FROM inventory.ga_categories WHERE id = @id", new NpgsqlParameter("id", setup.CategoryIds[0])));
        Assert.Equal(0, await RunExpiryWorkerAsync()); // toplanacak bir şey kalmadı; sayaç iki kez düşmez
    }

    [Fact]
    public async Task User_can_hold_again_after_their_own_hold_expired_even_if_the_worker_has_not_run()
    {
        var (organizer, _, organizationId) = await OrganizerAsync("org-a@loca.test", "Kumru Sahne");
        var setup = await CatalogSetup.SeatedSessionAsync(organizer, organizationId, Clock.Now.AddDays(10));
        var (customer, _) = await SignedInAsync("musteri@loca.test");
        var first = (await customer.HoldSeatsAsync(setup.SessionId, [setup.SeatIds[4], setup.SeatIds[7]])).HoldId();
        (await customer.HoldSeatsAsync(setup.SessionId, [setup.SeatIds[1]])).ShouldFail(HttpStatusCode.Conflict, "active_hold_exists");

        Clock.Advance(TimeSpan.FromSeconds(600));
        // Yeni seçim eski hold'un bir koltuğunu içeriyor, diğerini içermiyor.
        var second = (await customer.HoldSeatsAsync(setup.SessionId, [setup.SeatIds[2], setup.SeatIds[4]])).HoldId();

        Assert.Equal("Expired", await StatusAsync(first));
        Assert.Equal("Active", await StatusAsync(second));
        Assert.Equal(2, await Database.ScalarAsAdminAsync<long>("SELECT count(*) FROM inventory.session_seats WHERE hold_reservation_id = @r AND status = 'Held'", new NpgsqlParameter("r", second)));
        Assert.Equal("Available", await Database.ScalarAsAdminAsync<string>("SELECT status FROM inventory.session_seats WHERE id = @id", new NpgsqlParameter("id", setup.SeatIds[7])));
    }

    [Fact]
    public async Task Releasing_a_hold_frees_inventory_immediately_and_only_once()
    {
        var (organizer, _, organizationId) = await OrganizerAsync("org-a@loca.test", "Kumru Sahne");
        var setup = await CatalogSetup.GaSessionAsync(organizer, organizationId, Clock.Now.AddDays(10), [("Genel Giriş", 25000, 4)]);
        var customers = await CustomersAsync(2);
        var hold = (await customers[0].HoldGaAsync(setup.SessionId, null, (setup.CategoryIds[0], 4))).HoldId();

        (await customers[0].DeleteAsync($"/api/holds/{hold}")).ShouldBe(HttpStatusCode.NoContent);
        (await customers[0].DeleteAsync($"/api/holds/{hold}")).ShouldFail(HttpStatusCode.Conflict, "hold_not_active");

        Assert.Equal("Released", await StatusAsync(hold));
        Assert.Equal(0, await Database.ScalarAsAdminAsync<long>("SELECT held_count FROM inventory.ga_categories WHERE id = @id", new NpgsqlParameter("id", setup.CategoryIds[0])));
        (await customers[1].HoldGaAsync(setup.SessionId, null, (setup.CategoryIds[0], 4))).ShouldBe(HttpStatusCode.Created);
    }
}

/// <summary>Üretimdeki saat yolu: ManualClock olmadan, gerçek veritabanı saatiyle.</summary>
public sealed class DatabaseClockTests(PostgresFixture postgres) : IntegrationTest(postgres)
{
    protected override bool UseManualClock => false;

    [Fact]
    public async Task Expiry_is_computed_from_the_database_clock()
    {
        var (organizer, _, organizationId) = await OrganizerAsync("org-a@loca.test", "Kumru Sahne");
        var venueId = await CatalogSetup.VenueAsync(organizer, organizationId);
        var (eventId, _) = await CatalogSetup.EventAsync(organizer, organizationId);
        var sessionId = await CatalogSetup.DraftSessionAsync(organizer, eventId, venueId, DateTimeOffset.UtcNow.AddDays(10), "GeneralAdmission");
        var category = (await organizer.PostAsync($"/api/organizer/sessions/{sessionId}/categories", new { name = "Genel", priceMinor = 10000, capacity = 5 }))
            .ShouldBe(HttpStatusCode.OK).Json["categories"]![0]!["id"]!.GetValue<Guid>();
        (await organizer.PostAsync($"/api/organizer/sessions/{sessionId}/publish")).ShouldBe(HttpStatusCode.OK);
        var (customer, _) = await SignedInAsync("musteri@loca.test");

        var before = await Database.ScalarAsAdminAsync<DateTime>("SELECT clock_timestamp()");
        var hold = (await customer.HoldGaAsync(sessionId, null, (category, 1))).ShouldBe(HttpStatusCode.Created);
        var after = await Database.ScalarAsAdminAsync<DateTime>("SELECT clock_timestamp()");

        var serverTime = hold.Json["serverTime"]!.GetValue<DateTimeOffset>().UtcDateTime;
        var expiresAt = hold.Json["expiresAt"]!.GetValue<DateTimeOffset>().UtcDateTime;
        Assert.InRange(serverTime, before.ToUniversalTime(), after.ToUniversalTime());
        Assert.Equal(TimeSpan.FromSeconds(600), expiresAt - serverTime);
        Assert.Equal(0, expiresAt.Ticks % 10); // mikrosaniye çözünürlüğü: PostgreSQL ile gidiş-dönüşte kayıp olmaz
        Assert.Equal(expiresAt, (await Database.ScalarAsAdminAsync<DateTime>("SELECT expires_at FROM inventory.reservations")).ToUniversalTime());
    }
}

public sealed class IdempotencyTests(PostgresFixture postgres) : IntegrationTest(postgres)
{
    protected override IDictionary<string, string?> Settings => new Dictionary<string, string?> { ["Transactions:LockTimeoutMs"] = "600" };

    private async Task<(SeatedSessionSetup Setup, TestClient Customer, Guid UserId)> ArrangeAsync()
    {
        var (organizer, _, organizationId) = await OrganizerAsync("org-a@loca.test", "Kumru Sahne");
        var setup = await CatalogSetup.SeatedSessionAsync(organizer, organizationId, Clock.Now.AddDays(10));
        var (customer, userId) = await SignedInAsync("musteri@loca.test");
        return (setup, customer, userId);
    }

    private Task<long> ReservationCountAsync() => Database.ScalarAsAdminAsync<long>("SELECT count(*) FROM inventory.reservations");

    [Fact]
    public async Task Replay_returns_the_same_status_headers_and_body_bytes_without_a_second_effect()
    {
        var (setup, customer, _) = await ArrangeAsync();
        var key = HoldApi.NewKey();

        var first = (await customer.HoldSeatsAsync(setup.SessionId, [setup.SeatIds[0]], key)).ShouldBe(HttpStatusCode.Created);
        Clock.Advance(TimeSpan.FromSeconds(30)); // tekrar oynatılan yanıt yeniden hesaplanmaz: serverTime ilk andaki değerdir
        var replay = (await customer.HoldSeatsAsync(setup.SessionId, [setup.SeatIds[0]], key)).ShouldBe(HttpStatusCode.Created);

        Assert.Equal(first.Body, replay.Body);
        Assert.Equal(first.Header("Location"), replay.Header("Location"));
        Assert.Equal(first.ContentHeaders.ContentType?.ToString(), replay.ContentHeaders.ContentType?.ToString());
        Assert.Null(first.Header("Idempotent-Replayed"));
        Assert.Equal("true", replay.Header("Idempotent-Replayed"));
        Assert.Equal(1, await ReservationCountAsync());
        Assert.Equal(1, await Database.ScalarAsAdminAsync<long>("SELECT count(*) FROM ops.audit_log WHERE action = 'hold.created'"));

        // Saklanan gövde bayt dizisidir (jsonb değil) ve yanıtla birebir aynıdır.
        var stored = await Factory.WithDbAsync(db => db.IdempotencyRecords.AsNoTracking().SingleAsync());
        Assert.Equal(first.Body, stored.ResponseBody);
        Assert.Equal(201, stored.ResponseStatus);
        Assert.Equal("bytea", await Database.ScalarAsAdminAsync<string>(
            "SELECT data_type FROM information_schema.columns WHERE table_schema = 'ops' AND table_name = 'idempotency_records' AND column_name = 'response_body'"));
    }

    [Fact]
    public async Task Canonically_equal_body_replays_and_a_different_body_with_the_same_key_conflicts()
    {
        var (organizer, _, organizationId) = await OrganizerAsync("org-a@loca.test", "Kumru Sahne");
        var setup = await CatalogSetup.GaSessionAsync(organizer, organizationId, Clock.Now.AddDays(10), [("Genel Giriş", 25000, 50)]);
        var (customer, _) = await SignedInAsync("musteri@loca.test");
        var key = HoldApi.NewKey();
        var url = $"/api/sessions/{setup.SessionId}/holds";
        var category = setup.CategoryIds[0];

        var first = (await customer.PostAsync(url, $$"""{"items":[{"categoryId":"{{category}}","quantity":2}]}""", key)).ShouldBe(HttpStatusCode.Created);
        var reformatted = (await customer.PostAsync(url, $$"""
            { "items" : [ { "quantity" : 2, "categoryId" : "{{category}}" } ] }
            """, key)).ShouldBe(HttpStatusCode.Created);
        Assert.Equal(first.Body, reformatted.Body);
        Assert.Equal("true", reformatted.Header("Idempotent-Replayed"));

        (await customer.PostAsync(url, $$"""{"items":[{"categoryId":"{{category}}","quantity":3}]}""", key))
            .ShouldFail(HttpStatusCode.Conflict, "idempotency_key_reused");
        Assert.Equal(2, await Database.ScalarAsAdminAsync<long>("SELECT held_count FROM inventory.ga_categories WHERE id = @id", new NpgsqlParameter("id", category)));
        Assert.Equal(1, await ReservationCountAsync());
    }

    [Theory]
    [InlineData(null, "idempotency_key_required")]
    [InlineData("short", "idempotency_key_invalid")]
    [InlineData("has spaces in the key 123", "idempotency_key_invalid")]
    public async Task Key_is_required_and_validated(string? key, string code)
    {
        var (setup, customer, _) = await ArrangeAsync();
        (await customer.PostAsync($"/api/sessions/{setup.SessionId}/holds", new { seatIds = new[] { setup.SeatIds[0] } }, key)).ShouldFail(HttpStatusCode.BadRequest, code);
        Assert.Equal(0, await ReservationCountAsync());
    }

    [Fact]
    public async Task Keys_are_scoped_per_user()
    {
        var (setup, customer, _) = await ArrangeAsync();
        var (other, _) = await SignedInAsync("baskasi@loca.test");
        var key = HoldApi.NewKey();

        var mine = (await customer.HoldSeatsAsync(setup.SessionId, [setup.SeatIds[0]], key)).ShouldBe(HttpStatusCode.Created);
        // Başka kullanıcı aynı anahtarı ve aynı gövdeyi gönderse de benim yanıtımı alamaz; kendi isteği yürütülür.
        (await other.HoldSeatsAsync(setup.SessionId, [setup.SeatIds[0]], key)).ShouldFail(HttpStatusCode.Conflict, "seats_unavailable");
        var theirs = (await other.HoldSeatsAsync(setup.SessionId, [setup.SeatIds[1]], HoldApi.NewKey())).ShouldBe(HttpStatusCode.Created);

        Assert.NotEqual(mine.HoldId(), theirs.HoldId());
        Assert.Equal(2, await Database.ScalarAsAdminAsync<long>("SELECT count(*) FROM ops.idempotency_records"));
    }

    [Fact]
    public async Task Failed_attempt_is_not_stored_so_the_same_key_can_succeed_later()
    {
        var (setup, customer, _) = await ArrangeAsync();
        var (other, _) = await SignedInAsync("baskasi@loca.test");
        var blocker = (await other.HoldSeatsAsync(setup.SessionId, [setup.SeatIds[0]])).HoldId();
        var key = HoldApi.NewKey();

        (await customer.HoldSeatsAsync(setup.SessionId, [setup.SeatIds[0]], key)).ShouldFail(HttpStatusCode.Conflict, "seats_unavailable");
        Assert.Equal(1, await Database.ScalarAsAdminAsync<long>("SELECT count(*) FROM ops.idempotency_records"));

        (await other.DeleteAsync($"/api/holds/{blocker}")).ShouldBe(HttpStatusCode.NoContent);
        var success = (await customer.HoldSeatsAsync(setup.SessionId, [setup.SeatIds[0]], key)).ShouldBe(HttpStatusCode.Created);
        Assert.Null(success.Header("Idempotent-Replayed"));
    }

    [Fact]
    public async Task When_the_response_is_lost_after_commit_the_same_key_returns_the_original_result()
    {
        var (setup, customer, _) = await ArrangeAsync();
        var key = HoldApi.NewKey();
        Failpoints.FailOnce("idempotency.after-commit");

        // Transaction commit oldu, yanıt yazılamadan istek öldü.
        (await customer.HoldSeatsAsync(setup.SessionId, [setup.SeatIds[0]], key)).ShouldBe(HttpStatusCode.InternalServerError);
        Assert.Equal(1, await ReservationCountAsync());

        var recovered = (await customer.HoldSeatsAsync(setup.SessionId, [setup.SeatIds[0]], key)).ShouldBe(HttpStatusCode.Created);
        Assert.Equal("true", recovered.Header("Idempotent-Replayed"));
        Assert.Equal(await Database.ScalarAsAdminAsync<Guid>("SELECT id FROM inventory.reservations"), recovered.HoldId());
        Assert.Equal(1, await ReservationCountAsync());
        Assert.Equal(recovered.Body, (await Factory.WithDbAsync(db => db.IdempotencyRecords.AsNoTracking().SingleAsync())).ResponseBody);
    }

    [Fact]
    public async Task Fifteen_concurrent_requests_with_the_same_key_create_one_hold_and_return_identical_bytes()
    {
        var (setup, customer, _) = await ArrangeAsync();
        var key = HoldApi.NewKey();

        var responses = await Task.WhenAll(Enumerable.Range(0, 15).Select(_ => Task.Run(() => customer.HoldSeatsAsync(setup.SessionId, [setup.SeatIds[0], setup.SeatIds[1]], key))));

        Assert.All(responses, r => r.ShouldBe(HttpStatusCode.Created));
        Assert.All(responses, r => Assert.Equal(responses[0].Body, r.Body));
        Assert.Equal(1, responses.Count(r => r.Header("Idempotent-Replayed") is null));
        Assert.Equal(1, await ReservationCountAsync());
        Assert.Equal(2, await Database.ScalarAsAdminAsync<long>("SELECT count(*) FROM inventory.session_seats WHERE status = 'Held'"));
    }

    [Fact]
    public async Task Replay_requires_current_authentication()
    {
        var (setup, customer, userId) = await ArrangeAsync();
        var key = HoldApi.NewKey();
        (await customer.HoldSeatsAsync(setup.SessionId, [setup.SeatIds[0]], key)).ShouldBe(HttpStatusCode.Created);

        var loggedOut = customer.Clone();
        (await loggedOut.PostAsync("/api/auth/logout")).ShouldBe(HttpStatusCode.NoContent);
        (await customer.HoldSeatsAsync(setup.SessionId, [setup.SeatIds[0]], key)).ShouldFail(HttpStatusCode.Unauthorized, "unauthenticated");

        var again = Factory.NewClient();
        (await again.LoginAsync("musteri@loca.test")).ShouldBe(HttpStatusCode.OK);
        (await again.HoldSeatsAsync(setup.SessionId, [setup.SeatIds[0]], key)).ShouldBe(HttpStatusCode.Created); // aynı kullanıcı, yeni oturum: tekrar oynatılır

        await Database.ExecuteAsAdminAsync("UPDATE identity.users SET disabled_at = now() WHERE id = @id", new NpgsqlParameter("id", userId));
        (await again.HoldSeatsAsync(setup.SessionId, [setup.SeatIds[0]], key)).ShouldFail(HttpStatusCode.Unauthorized, "unauthenticated");
    }

    [Fact]
    public async Task Expired_record_is_ignored_and_the_request_executes_again()
    {
        var (setup, customer, _) = await ArrangeAsync();
        var key = HoldApi.NewKey();
        var first = (await customer.HoldSeatsAsync(setup.SessionId, [setup.SeatIds[0]], key)).HoldId();

        // Oturumun mutlak ömrü 12 saattir; idempotency kapsamı kullanıcıdır, oturum değil — yeni oturum aynı kaydı görür.
        Clock.Advance(TimeSpan.FromHours(24) - TimeSpan.FromSeconds(1));
        await customer.ReloginAsync("musteri@loca.test");
        Assert.Equal(first, (await customer.HoldSeatsAsync(setup.SessionId, [setup.SeatIds[0]], key)).HoldId());

        Clock.Advance(TimeSpan.FromSeconds(1)); // TTL doldu: kayıt yok sayılır, istek yeniden yürütülür (eski hold çoktan süresi dolmuş)
        var executedAgain = (await customer.HoldSeatsAsync(setup.SessionId, [setup.SeatIds[0]], key)).ShouldBe(HttpStatusCode.Created);
        Assert.NotEqual(first, executedAgain.HoldId());
        Assert.Null(executedAgain.Header("Idempotent-Replayed"));
        Assert.Equal(1, await Database.ScalarAsAdminAsync<long>("SELECT count(*) FROM ops.idempotency_records"));
    }

    [Fact]
    public async Task Request_blocked_by_an_uncommitted_claim_for_the_same_key_times_out_as_in_progress()
    {
        var (setup, customer, userId) = await ArrangeAsync();
        var key = HoldApi.NewKey();

        // Başka bir transaction aynı anahtarı talep etmiş ama henüz commit etmemiş.
        await using var blocker = new NpgsqlConnection(Postgres.AdminConnectionString(Database.Name));
        await blocker.OpenAsync();
        await using var tx = await blocker.BeginTransactionAsync();
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO ops.idempotency_records (user_id, operation, idempotency_key, hash_version, request_hash, created_at, expires_at)
            VALUES (@u, 'hold.create', @k, 1, repeat('0', 64), now(), now() + interval '1 day')
            """, blocker, tx))
        {
            insert.Parameters.AddWithValue("u", userId);
            insert.Parameters.AddWithValue("k", key);
            await insert.ExecuteNonQueryAsync();
        }

        var blocked = (await customer.HoldSeatsAsync(setup.SessionId, [setup.SeatIds[0]], key)).ShouldFail(HttpStatusCode.Conflict, "request_in_progress");
        Assert.Equal(1, blocked.Json["retryAfterSeconds"]!.GetValue<int>());
        await tx.RollbackAsync();

        // Talep geri alındı; takılı kayıt kalmadı ve aynı anahtar normal yürütülür.
        (await customer.HoldSeatsAsync(setup.SessionId, [setup.SeatIds[0]], key)).ShouldBe(HttpStatusCode.Created);
        Assert.Equal(1, await ReservationCountAsync());
    }
}

public sealed class TransactionRetryTests(PostgresFixture postgres) : IntegrationTest(postgres)
{
    private long Retries => Factory.Services.GetRequiredService<TransactionStats>().Retries;

    [Fact]
    public async Task Deadlock_victim_transaction_is_retried_and_the_hold_still_succeeds()
    {
        var (organizer, _, organizationId) = await OrganizerAsync("org-a@loca.test", "Kumru Sahne");
        var setup = await CatalogSetup.SeatedSessionAsync(organizer, organizationId, Clock.Now.AddDays(10));
        var (customer, _) = await SignedInAsync("musteri@loca.test");
        var ordered = setup.SeatIds.Take(2).Order().ToArray();
        var (low, high) = (ordered[0], ordered[1]);

        await using var rival = new NpgsqlConnection(Postgres.AdminConnectionString(Database.Name));
        await rival.OpenAsync();
        await using var tx = await rival.BeginTransactionAsync();
        async Task LockSeat(Guid id)
        {
            await using var command = new NpgsqlCommand("SELECT 1 FROM inventory.session_seats WHERE id = @id FOR UPDATE", rival, tx);
            command.Parameters.AddWithValue("id", id);
            await command.ExecuteScalarAsync();
        }

        // 1) Rakip yüksek kimlikli koltuğu kilitler. 2) Uygulama düşük kimlikliyi alır, yükseği bekler.
        // 3) Rakip düşük kimlikliyi ister → döngü. PostgreSQL ilk beklemeye başlayanı (uygulamayı) kurban seçer.
        await LockSeat(high);
        var request = Task.Run(() => customer.HoldSeatsAsync(setup.SessionId, [low, high]));
        await LockWaits.UntilSomeoneWaitsAsync(Database, "inventory.session_seats");
        await LockSeat(low); // Bu çağrının dönmesi, uygulamanın transaction'ının iptal edildiğini gösterir.
        await tx.RollbackAsync();

        (await request).ShouldBe(HttpStatusCode.Created);
        Assert.True(Retries >= 1, "The application transaction should have been chosen as the deadlock victim and retried.");
        Assert.Equal(1, await Database.ScalarAsAdminAsync<long>("SELECT count(*) FROM inventory.reservations"));
        Assert.Equal(2, await Database.ScalarAsAdminAsync<long>("SELECT count(*) FROM inventory.session_seats WHERE status = 'Held'"));
    }

    [Fact]
    public async Task Retry_budget_is_bounded_and_exhaustion_is_reported_as_unavailable()
    {
        var attempts = 0;
        var error = await Assert.ThrowsAsync<ApiException>(() => Factory.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<Loca.Api.Infrastructure.Persistence.AppDbContext>();
            return await sp.GetRequiredService<ITransactionRunner>().RunAsync(async ct =>
            {
                attempts++;
                await db.Database.ExecuteSqlRawAsync("DO $$ BEGIN RAISE EXCEPTION 'forced deadlock' USING ERRCODE = '40P01'; END $$", ct);
                return 0;
            }, CancellationToken.None);
        }));

        Assert.Equal(503, error.Status);
        Assert.Equal("service_unavailable", error.Code);
        Assert.Equal(3, attempts);
        Assert.Equal(2, Retries);
    }

    [Fact]
    public async Task Serialization_failure_on_the_first_attempt_succeeds_on_the_second_and_leaves_no_partial_write()
    {
        var attempts = 0;
        var result = await Factory.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<Loca.Api.Infrastructure.Persistence.AppDbContext>();
            return await sp.GetRequiredService<ITransactionRunner>().RunAsync(async ct =>
            {
                attempts++;
                await db.Database.ExecuteSqlRawAsync(
                    "INSERT INTO ops.audit_log (occurred_at, actor_kind, action, entity_type, entity_id) VALUES (now(), 'System', 'retry.probe', 'test', '1')", ct);
                if (attempts == 1)
                    await db.Database.ExecuteSqlRawAsync("DO $$ BEGIN RAISE EXCEPTION 'forced' USING ERRCODE = '40001'; END $$", ct);
                return attempts;
            }, CancellationToken.None);
        });

        Assert.Equal(2, result);
        Assert.Equal(1, Retries);
        Assert.Equal(1, await Database.ScalarAsAdminAsync<long>("SELECT count(*) FROM ops.audit_log WHERE action = 'retry.probe'"));
    }
}
