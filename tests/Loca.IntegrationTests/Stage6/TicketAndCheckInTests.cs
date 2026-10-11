using System.Net;
using System.Text.Json.Nodes;
using Loca.Api.Modules.Tickets;
using Loca.Domain;
using Loca.IntegrationTests.Infrastructure;
using Loca.IntegrationTests.Stage5;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Loca.IntegrationTests.Stage6;

/// <summary>Bilet testlerinin düzeneği: check-in penceresi açık bir seans, ödenmiş sipariş ve atanmış kapı görevlisi.</summary>
public abstract class TicketTest(PostgresFixture postgres) : PurchaseTest(postgres)
{
    protected TestClient Gate { get; private set; } = null!;
    protected Guid GateUserId { get; private set; }

    /// <summary>Seans 2 saat sonra başlar: check-in penceresi (başlangıçtan 3 saat önce) şu an açıktır.</summary>
    protected async Task ArrangeGateAsync(TimeSpan? startsIn = null)
    {
        await ArrangeAsync(Clock.Now.Add(startsIn ?? TimeSpan.FromHours(2)));
        (Gate, GateUserId) = await SignedInAsync("kapi@loca.test");
        (await Organizer.PostAsync($"/api/organizer/organizations/{OrganizationId}/members", new { email = "kapi@loca.test", role = "GateStaff" })).ShouldBe(HttpStatusCode.OK);
        (await Organizer.PutAsync($"/api/organizer/sessions/{Seated.SessionId}/gate-staff/{GateUserId}")).ShouldBe(HttpStatusCode.NoContent);
    }

    protected async Task<Guid> PaidOrderAsync(TestClient? customer = null, params int[] seatIndexes)
    {
        var (_, orderId) = await StartCheckoutAsync("tok_success", customer, seatIndexes);
        await DrainAsync();
        Assert.Equal("Paid", await OrderStatusAsync(orderId));
        return orderId;
    }

    protected async Task<List<(Guid Id, string Token)>> TicketsWithTokensAsync(TestClient customer)
    {
        var list = (await customer.GetAsync("/api/tickets")).ShouldBe(HttpStatusCode.OK).Json.AsArray();
        var result = new List<(Guid, string)>();
        foreach (var item in list)
        {
            var id = item!["id"]!.GetValue<Guid>();
            var detail = (await customer.GetAsync($"/api/tickets/{id}")).ShouldBe(HttpStatusCode.OK);
            result.Add((id, detail.Json["token"]!.GetValue<string>()));
        }
        return result;
    }

    protected Task<ApiResponse> ScanAsync(TestClient staff, string token, string? key = null, Guid? sessionId = null, string? gate = "A") =>
        staff.PostAsync($"/api/gate/sessions/{sessionId ?? Seated.SessionId}/check-ins", new { token, gate }, key ?? HoldApi.NewKey());

    protected Task<string?> TicketStatusAsync(Guid ticketId) => ScalarAsync("SELECT status FROM tickets.tickets WHERE id = @id", P("id", ticketId));
}

public sealed class TicketIssuanceTests(PostgresFixture postgres) : TicketTest(postgres)
{
    [Fact]
    public async Task Tickets_are_issued_once_with_deterministic_identities_even_if_issuance_runs_repeatedly()
    {
        await ArrangeAsync();
        var orderId = await PaidOrderAsync(null, 0, 1);

        var ids = async () => (await Factory.WithDbAsync(db => Task.FromResult(db.Tickets.Where(t => t.OrderId == orderId).OrderBy(t => t.Id).Select(t => new { t.Id, t.ReservationLineId, t.Ordinal }).ToList())));
        var first = await ids();
        Assert.Equal(2, first.Count);
        Assert.All(first, t => Assert.Equal(TicketIdentity.For(orderId, t.ReservationLineId, t.Ordinal), t.Id));

        // Aynı niyet yeniden teslim edilir (outbox en az bir kez teslim eder) ve doğrudan da çağrılır.
        await Database.ExecuteAsAdminAsync("UPDATE ops.outbox_messages SET processed_at = NULL, lease_until = NULL WHERE type = 'order.paid'");
        await DrainAsync();
        Assert.Equal(0, await Factory.WithScopeAsync(sp => sp.GetRequiredService<TicketService>().IssueForOrderAsync(orderId, CancellationToken.None)));

        Assert.Equal(first, await ids());
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM ops.audit_log WHERE action = 'tickets.issued'"));
    }

    [Fact]
    public async Task Crash_between_payment_and_issuance_is_recovered_from_the_outbox()
    {
        await ArrangeAsync();
        var (_, orderId) = await StartCheckoutAsync("tok_success");
        await RunOutboxAsync(); // ödeme alındı; süreç bilet üretmeden "çöktü"

        Assert.Equal("Paid", await OrderStatusAsync(orderId));
        Assert.Equal(0, await TicketsAsync(orderId));
        Assert.Empty((await Customer.GetAsync("/api/tickets")).Json.AsArray());

        await DrainAsync(); // yeniden başlayan worker niyeti outbox'tan alır
        Assert.Equal(1, await TicketsAsync(orderId));
    }

    [Fact]
    public async Task General_admission_quantity_produces_one_ticket_per_admission()
    {
        var (organizer, _, organizationId) = await OrganizerAsync("org-ga@loca.test", "Martı Etkinlik");
        var ga = await CatalogSetup.GaSessionAsync(organizer, organizationId, Clock.Now.AddDays(5), [("Genel Giriş", 25000, 50), ("Sahne Önü", 40000, 10)]);
        var (customer, _) = await SignedInAsync("ga-musteri@loca.test");
        var holdId = (await customer.HoldGaAsync(ga.SessionId, null, (ga.CategoryIds[0], 3), (ga.CategoryIds[1], 1))).HoldId();
        var orderId = (await customer.CheckoutAsync(holdId)).OrderId();
        await DrainAsync();

        var tickets = (await customer.GetAsync("/api/tickets")).ShouldBe(HttpStatusCode.OK).Json.AsArray();
        Assert.Equal(4, tickets.Count);
        Assert.Equal(["Genel Giriş · 1/3", "Genel Giriş · 2/3", "Genel Giriş · 3/3", "Sahne Önü"], tickets.Select(t => t!["label"]!.GetValue<string>()).Order(StringComparer.Ordinal));
        Assert.Equal(4, await CountAsync("SELECT count(DISTINCT token_hash) FROM tickets.tickets WHERE order_id = @o", new NpgsqlParameter("o", orderId)));
        Assert.Equal("3,3", await ScalarAsync("SELECT sold_count || ',' || (SELECT count(*) FROM tickets.tickets WHERE ga_category_id = c.id) FROM inventory.ga_categories c WHERE id = @id", new NpgsqlParameter("id", ga.CategoryIds[0])));
    }

    [Fact]
    public async Task No_ticket_is_issued_for_an_order_that_is_not_paid()
    {
        await ArrangeAsync();
        var (_, declined) = await StartCheckoutAsync("tok_decline");
        await DrainAsync();

        Assert.Equal(0, await Factory.WithScopeAsync(sp => sp.GetRequiredService<TicketService>().IssueForOrderAsync(declined, CancellationToken.None)));
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM tickets.tickets"));
    }

    [Fact]
    public async Task Token_is_a_random_bearer_secret_stored_only_as_a_hash_and_ciphertext()
    {
        await ArrangeAsync();
        await PaidOrderAsync(null, 0, 1);
        var tickets = await TicketsWithTokensAsync(Customer);

        Assert.Equal(2, tickets.Select(t => t.Token).Distinct().Count());
        foreach (var (id, token) in tickets)
        {
            Assert.True(TicketToken.IsWellFormed(token));
            Assert.DoesNotContain(id.ToString("N"), token, StringComparison.OrdinalIgnoreCase); // tahmin edilebilir kimlik içermez
            Assert.Equal(Convert.ToHexStringLower(TicketToken.Hash(token)), await ScalarAsync("SELECT encode(token_hash, 'hex') FROM tickets.tickets WHERE id = @id", P("id", id)));
            // Veritabanındaki hiçbir sütun token'ın düz metnini içermez.
            Assert.Equal(0, await CountAsync("SELECT count(*) FROM tickets.tickets t WHERE t::text LIKE '%' || @token || '%'", P("token", token)));
            Assert.Equal(0, await CountAsync("SELECT count(*) FROM ops.audit_log a WHERE a::text LIKE '%' || @token || '%'", P("token", token)));
            Assert.Equal(0, await CountAsync("SELECT count(*) FROM ops.idempotency_records r WHERE convert_from(r.response_body, 'UTF8') LIKE '%' || @token || '%'", P("token", token)));
        }
    }

    [Fact]
    public async Task Tickets_are_visible_only_to_their_owner_and_qr_responses_are_never_cached()
    {
        await ArrangeGateAsync();
        var orderId = await PaidOrderAsync();
        var (other, _) = await SignedInAsync("baskasi@loca.test");
        var ticket = (await TicketsWithTokensAsync(Customer)).Single();

        var list = (await Customer.GetAsync("/api/tickets")).ShouldBe(HttpStatusCode.OK);
        Assert.Equal("no-store", list.Header("Cache-Control"));
        Assert.DoesNotContain("token", list.Text, StringComparison.OrdinalIgnoreCase); // liste QR içermez
        Assert.DoesNotContain(ticket.Token, list.Text);

        var detail = (await Customer.GetAsync($"/api/tickets/{ticket.Id}")).ShouldBe(HttpStatusCode.OK);
        Assert.Equal("no-store", detail.Header("Cache-Control"));
        Assert.Equal(orderId, detail.Json["orderId"]!.GetValue<Guid>());
        Assert.Equal("Salon / A / 1", detail.Json["label"]!.GetValue<string>());
        foreach (var personal in new[] { "musteri@loca.test", "Demo musteri", "email", "displayName", "phone" })
            Assert.DoesNotContain(personal, detail.Text, StringComparison.OrdinalIgnoreCase);

        // Başkası, organizatör ve kapı görevlisi müşteri bilet uçlarından bu bileti göremez.
        foreach (var stranger in new[] { other, Organizer, Gate })
        {
            (await stranger.GetAsync($"/api/tickets/{ticket.Id}")).ShouldFail(HttpStatusCode.NotFound, "not_found");
            Assert.Empty((await stranger.GetAsync("/api/tickets")).Json.AsArray());
        }
        (await Factory.NewClient().GetAsync($"/api/tickets/{ticket.Id}")).ShouldFail(HttpStatusCode.Unauthorized, "unauthenticated");

        // Public ve organizatör/kapı yanıtları token içermez.
        foreach (var url in new[] { "/api/catalog/sessions", $"/api/catalog/sessions/{Seated.SessionId}", $"/api/catalog/sessions/{Seated.SessionId}/seats" })
            Assert.DoesNotContain(ticket.Token, (await other.GetAsync(url)).Text);
        Assert.DoesNotContain(ticket.Token, (await Organizer.GetAsync($"/api/organizer/sessions/{Seated.SessionId}")).Text);
        Assert.DoesNotContain(ticket.Token, (await Gate.GetAsync("/api/gate/sessions")).Text);
    }
}

public sealed class CheckInTests(PostgresFixture postgres) : TicketTest(postgres)
{
    [Fact]
    public async Task First_scan_is_accepted_replay_returns_the_same_bytes_and_a_second_scan_is_already_used()
    {
        await ArrangeGateAsync();
        await PaidOrderAsync();
        var ticket = (await TicketsWithTokensAsync(Customer)).Single();
        var key = HoldApi.NewKey();

        var accepted = (await ScanAsync(Gate, ticket.Token, key)).ShouldBe(HttpStatusCode.OK);
        Assert.Equal("Accepted", accepted.Json["result"]!.GetValue<string>());
        Assert.Equal("Salon / A / 1", accepted.Json["label"]!.GetValue<string>());
        Assert.Equal(Clock.Now, accepted.Json["checkedInAt"]!.GetValue<DateTimeOffset>());
        foreach (var personal in new[] { "musteri", "email", "orderId", "totalMinor" })
            Assert.DoesNotContain(personal, accepted.Text, StringComparison.OrdinalIgnoreCase); // kapı yanıtı asgari bilgidir

        // Ağ hatası sonrası aynı anahtarla yeniden deneme: ilk sonuç bayt bayt döner.
        Clock.Advance(TimeSpan.FromMinutes(1));
        var replay = (await ScanAsync(Gate, ticket.Token, key)).ShouldBe(HttpStatusCode.OK);
        Assert.Equal(accepted.Body, replay.Body);
        Assert.Equal("true", replay.Header("Idempotent-Replayed"));

        // Gerçek ikinci tarama (yeni anahtar): zaten kullanıldı + ilk girişin bilgisi.
        var again = (await ScanAsync(Gate, ticket.Token, gate: "B")).ShouldFail(HttpStatusCode.Conflict, "already_checked_in");
        Assert.Equal("AlreadyUsed", again.Json["result"]!.GetValue<string>());
        Assert.Equal("A", again.Json["gate"]!.GetValue<string>());
        Assert.Equal(accepted.Json["checkedInAt"]!.GetValue<DateTimeOffset>(), again.Json["checkedInAt"]!.GetValue<DateTimeOffset>());

        Assert.Equal("CheckedIn", await TicketStatusAsync(ticket.Id));
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM tickets.scan_log WHERE result = 'Accepted'"));
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM tickets.scan_log WHERE result = 'already_checked_in'"));
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM ops.audit_log WHERE action = 'ticket.checked_in'"));

        // Kullanılmış biletin QR'ı sahibine de yeniden verilmez.
        var detail = (await Customer.GetAsync($"/api/tickets/{ticket.Id}")).ShouldBe(HttpStatusCode.OK);
        Assert.Equal("CheckedIn", detail.Json["status"]!.GetValue<string>());
        Assert.Null(detail.Json["token"]);
    }

    [Fact]
    public async Task Two_gates_scanning_the_same_ticket_at_the_same_time_produce_exactly_one_acceptance()
    {
        await ArrangeGateAsync();
        await PaidOrderAsync();
        var ticket = (await TicketsWithTokensAsync(Customer)).Single();
        var (secondGate, secondGateId) = await SignedInAsync("kapi-2@loca.test");
        (await Organizer.PostAsync($"/api/organizer/organizations/{OrganizationId}/members", new { email = "kapi-2@loca.test", role = "GateStaff" })).ShouldBe(HttpStatusCode.OK);
        (await Organizer.PutAsync($"/api/organizer/sessions/{Seated.SessionId}/gate-staff/{secondGateId}")).ShouldBe(HttpStatusCode.NoContent);

        var responses = await Task.WhenAll(Enumerable.Range(0, 24).Select(i => Task.Run(() =>
            ScanAsync(i % 2 == 0 ? Gate : secondGate, ticket.Token, gate: i % 2 == 0 ? "A" : "B"))));

        Assert.Single(responses, r => r.Status == HttpStatusCode.OK);
        Assert.Equal(23, responses.Count(r => r.Status == HttpStatusCode.Conflict && r.Code == "already_checked_in"));
        Assert.Equal("CheckedIn", await TicketStatusAsync(ticket.Id));
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM tickets.scan_log WHERE result = 'Accepted'"));
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM ops.audit_log WHERE action = 'ticket.checked_in'"));
    }

    [Fact]
    public async Task Ticket_of_another_session_or_another_organizer_is_rejected_without_revealing_where_it_belongs()
    {
        await ArrangeGateAsync();
        await PaidOrderAsync();
        var myTicket = (await TicketsWithTokensAsync(Customer)).Single();

        // Aynı organizasyonun başka seansı.
        var sibling = await CatalogSetup.SeatedSessionAsync(Organizer, OrganizationId, Clock.Now.AddHours(2), title: "Kardeş Seans");
        (await Organizer.PutAsync($"/api/organizer/sessions/{sibling.SessionId}/gate-staff/{GateUserId}")).ShouldBe(HttpStatusCode.NoContent);
        var wrong = (await ScanAsync(Gate, myTicket.Token, sessionId: sibling.SessionId)).ShouldFail(HttpStatusCode.Conflict, "wrong_session");
        Assert.DoesNotContain(Seated.SessionId.ToString(), wrong.Text);
        Assert.DoesNotContain("Salon", wrong.Text);

        // Başka organizatörün seansı ve kapı görevlisi.
        var (otherOrganizer, otherOrganizerId, otherOrganization) = await OrganizerAsync("org-b@loca.test", "Martı Etkinlik");
        var foreign = await CatalogSetup.SeatedSessionAsync(otherOrganizer, otherOrganization, Clock.Now.AddHours(2), title: "Yabancı Seans");
        (await otherOrganizer.PutAsync($"/api/organizer/sessions/{foreign.SessionId}/gate-staff/{otherOrganizerId}")).ShouldBe(HttpStatusCode.NoContent);
        (await ScanAsync(otherOrganizer, myTicket.Token, sessionId: foreign.SessionId)).ShouldFail(HttpStatusCode.Conflict, "wrong_session");

        // Yabancı görevli benim seansımda tarayamaz; benim görevlim yabancı seansta tarayamaz (seans "yok" görünür).
        (await ScanAsync(otherOrganizer, myTicket.Token)).ShouldFail(HttpStatusCode.NotFound, "not_found");
        (await ScanAsync(Gate, myTicket.Token, sessionId: foreign.SessionId)).ShouldFail(HttpStatusCode.NotFound, "not_found");
        // Yabancı organizatör, benim görevlimi kendi seansına ya da kendisini benim seansıma atayamaz.
        (await otherOrganizer.PutAsync($"/api/organizer/sessions/{foreign.SessionId}/gate-staff/{GateUserId}")).ShouldFail(HttpStatusCode.UnprocessableEntity, "user_not_found");
        (await otherOrganizer.PutAsync($"/api/organizer/sessions/{Seated.SessionId}/gate-staff/{otherOrganizerId}")).ShouldFail(HttpStatusCode.NotFound, "not_found");

        Assert.Equal("Valid", await TicketStatusAsync(myTicket.Id));
        (await ScanAsync(Gate, myTicket.Token)).ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Unassigned_or_removed_staff_cannot_scan_and_cannot_replay_an_earlier_acceptance()
    {
        await ArrangeGateAsync();
        await PaidOrderAsync(null, 0, 1);
        var tickets = await TicketsWithTokensAsync(Customer);
        var key = HoldApi.NewKey();
        (await ScanAsync(Gate, tickets[0].Token, key)).ShouldBe(HttpStatusCode.OK);

        // Müşteri ve organizatör yöneticisi (kendini atamadıkça) tarama yapamaz.
        (await ScanAsync(Customer, tickets[1].Token)).ShouldFail(HttpStatusCode.NotFound, "not_found");
        (await ScanAsync(Organizer, tickets[1].Token)).ShouldFail(HttpStatusCode.NotFound, "not_found");

        (await Organizer.DeleteAsync($"/api/organizer/sessions/{Seated.SessionId}/gate-staff/{GateUserId}")).ShouldBe(HttpStatusCode.NoContent);
        // Atama kaldırıldı: yeni tarama da, eski başarılı yanıtın aynı anahtarla tekrarı da reddedilir.
        (await ScanAsync(Gate, tickets[1].Token)).ShouldFail(HttpStatusCode.NotFound, "not_found");
        (await ScanAsync(Gate, tickets[0].Token, key)).ShouldFail(HttpStatusCode.NotFound, "not_found");
        Assert.Empty((await Gate.GetAsync("/api/gate/sessions")).Json.AsArray());

        // Atama geri gelse bile organizasyon üyeliği kaldırılmışsa yetki yoktur.
        (await Organizer.PutAsync($"/api/organizer/sessions/{Seated.SessionId}/gate-staff/{GateUserId}")).ShouldBe(HttpStatusCode.NoContent);
        (await ScanAsync(Gate, tickets[0].Token, key)).ShouldBe(HttpStatusCode.OK);
        (await Organizer.DeleteAsync($"/api/organizer/organizations/{OrganizationId}/members/{GateUserId}")).ShouldBe(HttpStatusCode.NoContent);
        (await ScanAsync(Gate, tickets[1].Token)).ShouldFail(HttpStatusCode.NotFound, "not_found");
        Assert.Equal("Valid", await TicketStatusAsync(tickets[1].Id));
    }

    [Fact]
    public async Task Check_in_is_only_possible_inside_the_window_around_the_session_start()
    {
        await ArrangeGateAsync(startsIn: TimeSpan.FromHours(4)); // pencere 1 saat sonra açılır
        await PaidOrderAsync(null, 0, 1);
        var tickets = await TicketsWithTokensAsync(Customer);

        var early = (await ScanAsync(Gate, tickets[0].Token)).ShouldFail(HttpStatusCode.Conflict, "outside_checkin_window");
        Assert.Equal(Clock.Now.AddHours(1), early.Json["opensAt"]!.GetValue<DateTimeOffset>());
        Assert.Equal("Valid", await TicketStatusAsync(tickets[0].Id));

        Clock.Advance(TimeSpan.FromHours(1)); // now == opensAt → açık
        (await ScanAsync(Gate, tickets[0].Token)).ShouldBe(HttpStatusCode.OK);

        Clock.Advance(TimeSpan.FromHours(9) - TimeSpan.FromMicroseconds(1)); // kapanıştan 1 µs önce
        await Gate.ReloginAsync("kapi@loca.test");
        var gateSessions = (await Gate.GetAsync("/api/gate/sessions")).ShouldBe(HttpStatusCode.OK).Json.AsArray();
        Assert.True(gateSessions.Single()!["windowOpen"]!.GetValue<bool>());
        Clock.Advance(TimeSpan.FromMicroseconds(1)); // now == closesAt → kapalı
        (await ScanAsync(Gate, tickets[1].Token)).ShouldFail(HttpStatusCode.Conflict, "outside_checkin_window");
        Assert.Equal("Valid", await TicketStatusAsync(tickets[1].Id));
    }

    [Fact]
    public async Task Cancelled_ticket_and_cancelled_session_are_rejected_with_their_own_reasons()
    {
        await ArrangeGateAsync();
        await PaidOrderAsync(null, 0, 1);
        var tickets = await TicketsWithTokensAsync(Customer);

        await Database.ExecuteAsAdminAsync("UPDATE tickets.tickets SET status = 'Cancelled', voided_at = now(), void_reason = 'Test' WHERE id = @id", P("id", tickets[0].Id));
        var voided = (await ScanAsync(Gate, tickets[0].Token)).ShouldFail(HttpStatusCode.Conflict, "ticket_void");
        Assert.Equal("Invalid", voided.Json["result"]!.GetValue<string>());

        await Database.ExecuteAsAdminAsync("UPDATE catalog.sessions SET status = 'Cancelled' WHERE id = @id", P("id", Seated.SessionId));
        (await ScanAsync(Gate, tickets[1].Token)).ShouldFail(HttpStatusCode.Conflict, "session_cancelled");
        Assert.Equal("Valid", await TicketStatusAsync(tickets[1].Id));
    }

    [Theory]
    [InlineData("LT1.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")] // biçimi doğru, böyle bir bilet yok
    [InlineData("not-a-ticket-token")]
    [InlineData("")]
    public async Task Unknown_or_malformed_token_is_not_found_and_is_logged_without_the_token(string token)
    {
        await ArrangeGateAsync();
        await PaidOrderAsync();

        var response = (await ScanAsync(Gate, token)).ShouldFail(HttpStatusCode.NotFound, "ticket_not_found");
        Assert.Equal("Invalid", response.Json["result"]!.GetValue<string>());
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM tickets.scan_log WHERE result = 'ticket_not_found' AND ticket_id IS NULL"));
        if (token.Length > 8)
            Assert.Equal(0, await CountAsync("SELECT count(*) FROM tickets.scan_log s WHERE s::text LIKE '%' || @t || '%'", P("t", token)));
        (await Gate.PostAsync($"/api/gate/sessions/{Seated.SessionId}/check-ins", new { token })).ShouldFail(HttpStatusCode.BadRequest, "idempotency_key_required");
    }

    [Fact]
    public async Task Failure_before_commit_never_reports_success_and_the_same_key_then_completes_the_check_in()
    {
        await ArrangeGateAsync();
        await PaidOrderAsync();
        var ticket = (await TicketsWithTokensAsync(Customer)).Single();
        var key = HoldApi.NewKey();
        Failpoints.FailOnce("checkin.before-commit");

        // İşlem commit olamadan öldü: istemci başarı görmez, bilet kullanılmamış kalır.
        (await ScanAsync(Gate, ticket.Token, key)).ShouldBe(HttpStatusCode.InternalServerError);
        Assert.Equal("Valid", await TicketStatusAsync(ticket.Id));
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM tickets.scan_log WHERE result = 'Accepted'"));

        // Belirsiz sonucu çözmek için aynı anahtarla yeniden deneme.
        var retried = (await ScanAsync(Gate, ticket.Token, key)).ShouldBe(HttpStatusCode.OK);
        Assert.Null(retried.Header("Idempotent-Replayed"));
        Assert.Equal("CheckedIn", await TicketStatusAsync(ticket.Id));
    }

    [Fact]
    public async Task Accepted_but_response_lost_is_resolved_by_replaying_the_same_key()
    {
        await ArrangeGateAsync();
        await PaidOrderAsync();
        var ticket = (await TicketsWithTokensAsync(Customer)).Single();
        var key = HoldApi.NewKey();
        Failpoints.FailOnce("idempotency.after-commit");

        (await ScanAsync(Gate, ticket.Token, key)).ShouldBe(HttpStatusCode.InternalServerError); // commit oldu, yanıt kayboldu
        Assert.Equal("CheckedIn", await TicketStatusAsync(ticket.Id));

        var recovered = (await ScanAsync(Gate, ticket.Token, key)).ShouldBe(HttpStatusCode.OK); // "zaten kullanıldı" değil: ilk sonucun kendisi
        Assert.Equal("Accepted", recovered.Json["result"]!.GetValue<string>());
        Assert.Equal("true", recovered.Header("Idempotent-Replayed"));
    }

    [Fact]
    public async Task Gate_session_list_shows_only_assigned_sessions_with_counts_and_no_customer_data()
    {
        await ArrangeGateAsync();
        await PaidOrderAsync(null, 0, 1);
        var other = await CatalogSetup.SeatedSessionAsync(Organizer, OrganizationId, Clock.Now.AddHours(3), title: "Atanmamış Seans");
        var ticket = (await TicketsWithTokensAsync(Customer)).First();
        (await ScanAsync(Gate, ticket.Token)).ShouldBe(HttpStatusCode.OK);

        var sessions = (await Gate.GetAsync("/api/gate/sessions")).ShouldBe(HttpStatusCode.OK);
        var only = Assert.Single(sessions.Json.AsArray())!;
        Assert.Equal(Seated.SessionId, only["sessionId"]!.GetValue<Guid>());
        Assert.Equal(2, only["ticketsTotal"]!.GetValue<int>());
        Assert.Equal(1, only["ticketsCheckedIn"]!.GetValue<int>());
        Assert.True(only["windowOpen"]!.GetValue<bool>());
        Assert.DoesNotContain(other.SessionId.ToString(), sessions.Text);
        foreach (var forbidden in new[] { "musteri", "email", "totalMinor", "token", "orderNumber" })
            Assert.DoesNotContain(forbidden, sessions.Text, StringComparison.OrdinalIgnoreCase);

        // Kapı görevlisi organizatör uçlarına (satış, ekip, sipariş) erişemez.
        (await Gate.GetAsync($"/api/organizer/sessions/{Seated.SessionId}")).ShouldFail(HttpStatusCode.Forbidden, "forbidden");
        (await Gate.GetAsync($"/api/organizer/sessions/{Seated.SessionId}/gate-staff")).ShouldFail(HttpStatusCode.Forbidden, "forbidden");
        var staff = (await Organizer.GetAsync($"/api/organizer/sessions/{Seated.SessionId}/gate-staff")).ShouldBe(HttpStatusCode.OK).Json.AsArray();
        Assert.Contains(staff, s => s!["userId"]!.GetValue<Guid>() == GateUserId && s["assigned"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Database_constraints_keep_ticket_state_consistent()
    {
        await ArrangeGateAsync();
        var orderId = await PaidOrderAsync();
        var ticketId = (await TicketsWithTokensAsync(Customer)).Single().Id;

        foreach (var (sql, state) in new[]
        {
            ($"UPDATE tickets.tickets SET status = 'CheckedIn' WHERE id = '{ticketId}'", "23514"),              // giriş zamanı olmadan kullanılmış olamaz
            ($"UPDATE tickets.tickets SET status = 'Cancelled' WHERE id = '{ticketId}'", "23514"),              // iptal zamanı olmadan iptal olamaz
            ($"UPDATE tickets.tickets SET checked_in_at = now() WHERE id = '{ticketId}'", "23514"),             // geçerli bilet "girmiş" olamaz
            ($"INSERT INTO tickets.tickets (id, order_id, reservation_id, reservation_line_id, ordinal, session_id, organization_id, owner_user_id, session_seat_id, ga_category_id, label, status, token_hash, token_protected, issued_at) SELECT gen_random_uuid(), order_id, reservation_id, reservation_line_id, ordinal, session_id, organization_id, owner_user_id, session_seat_id, ga_category_id, label, status, sha256('x'), token_protected, issued_at FROM tickets.tickets WHERE id = '{ticketId}'", "23505"),
        })
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => Database.ExecuteAsAppAsync(sql));
            Assert.Equal(state, error.SqlState);
        }
        Assert.Equal(1, await TicketsAsync(orderId));
    }
}
