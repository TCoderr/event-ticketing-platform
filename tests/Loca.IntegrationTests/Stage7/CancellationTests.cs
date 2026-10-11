using System.Net;
using Loca.Api.Infrastructure.Workers;
using Loca.Api.Modules.Operations;
using Loca.Api.Modules.Orders;
using Loca.IntegrationTests.Infrastructure;
using Loca.IntegrationTests.Stage6;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Loca.IntegrationTests.Stage7;

public static class CancelApi
{
    public static Task<ApiResponse> CancelOrderAsync(this TestClient client, Guid orderId, string? key = null) =>
        client.PostAsync($"/api/orders/{orderId}/cancel", null, key ?? HoldApi.NewKey());

    public static Task<ApiResponse> CancelSessionAsync(this TestClient client, Guid sessionId, string? key = null) =>
        client.PostAsync($"/api/organizer/sessions/{sessionId}/cancel", null, key ?? HoldApi.NewKey());
}

public sealed class CustomerCancellationTests(PostgresFixture postgres) : TicketTest(postgres)
{
    [Fact]
    public async Task Cancelling_a_paid_order_voids_tickets_reopens_inventory_and_refunds_once()
    {
        await ArrangeAsync(Clock.Now.AddDays(10));
        var orderId = await PaidOrderAsync(null, 0, 1);
        var tickets = await TicketsWithTokensAsync(Customer);
        var key = HoldApi.NewKey();

        var cancelled = (await Customer.CancelOrderAsync(orderId, key)).ShouldBe(HttpStatusCode.OK);

        Assert.Equal("Cancelled", cancelled.Json.OrderStatus());
        Assert.Equal("Customer", cancelled.Json["cancelReason"]!.GetValue<string>());
        Assert.Equal("Pending", cancelled.Json.RefundStatus());
        Assert.Equal(90000, cancelled.Json["refund"]!["amountMinor"]!.GetValue<long>());
        Assert.False(cancelled.Json["canCancel"]!.GetValue<bool>());

        // Bilet, iade henüz "Pending" iken de geçersizdir; QR artık verilmez.
        Assert.All(tickets, t => Assert.Equal("Cancelled", TicketStatusAsync(t.Id).Result));
        Assert.Null((await Customer.GetAsync($"/api/tickets/{tickets[0].Id}")).Json["token"]);
        // Envanter iptal transaction'ında yeniden açıldı: başkası hemen tutabilir.
        Assert.Equal("Available", await SeatStatusAsync(0));
        var (other, _) = await SignedInAsync("baskasi@loca.test");
        (await other.HoldSeatsAsync(Seated.SessionId, [Seated.SeatIds[0], Seated.SeatIds[1]])).ShouldBe(HttpStatusCode.Created);

        // Aynı anahtar: ilk yanıt bayt bayt; yeni anahtar: sipariş artık ödenmiş değil.
        var replay = (await Customer.CancelOrderAsync(orderId, key)).ShouldBe(HttpStatusCode.OK);
        Assert.Equal(cancelled.Body, replay.Body);
        (await Customer.CancelOrderAsync(orderId)).ShouldFail(HttpStatusCode.Conflict, "order_not_paid");

        await DrainAsync();
        Assert.Equal("Succeeded", (await Customer.OrderAsync(orderId)).RefundStatus());
        Assert.Equal(1, await RefundsAsync(orderId));
        Assert.Equal(1, await ProviderRefundsAsync());
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM ops.audit_log WHERE action = 'order.cancelled_by_customer'"));
    }

    [Fact]
    public async Task General_admission_cancellation_returns_capacity_in_the_same_transaction()
    {
        var (organizer, _, organizationId) = await OrganizerAsync("org-ga@loca.test", "Martı Etkinlik");
        var ga = await CatalogSetup.GaSessionAsync(organizer, organizationId, Clock.Now.AddDays(5), [("Genel Giriş", 25000, 3)]);
        var (customer, _) = await SignedInAsync("ga-musteri@loca.test");
        var orderId = (await customer.CheckoutAsync((await customer.HoldGaAsync(ga.SessionId, null, (ga.CategoryIds[0], 3))).HoldId())).OrderId();
        await DrainAsync();
        Assert.Equal("0,3", await ScalarAsync("SELECT held_count || ',' || sold_count FROM inventory.ga_categories"));

        (await customer.CancelOrderAsync(orderId)).ShouldBe(HttpStatusCode.OK);

        Assert.Equal("0,0", await ScalarAsync("SELECT held_count || ',' || sold_count FROM inventory.ga_categories"));
        Assert.Equal(3, await CountAsync("SELECT count(*) FROM tickets.tickets WHERE status = 'Cancelled'"));
        var (other, _) = await SignedInAsync("ga-baskasi@loca.test");
        (await other.HoldGaAsync(ga.SessionId, null, (ga.CategoryIds[0], 3))).ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Cancellation_closes_exactly_at_the_cutoff_before_the_session_start()
    {
        await ArrangeAsync(Clock.Now.AddHours(30)); // iptal sınırı: başlangıçtan 24 saat önce → 6 saat sonra
        var first = await PaidOrderAsync(null, 0);
        var (other, _) = await SignedInAsync("baskasi@loca.test");
        var second = await PaidOrderAsync(other, 1);

        var order = await Customer.OrderAsync(first);
        Assert.True(order["canCancel"]!.GetValue<bool>());
        Assert.Equal(Clock.Now.AddHours(6), order["cancelDeadline"]!.GetValue<DateTimeOffset>());

        Clock.Advance(TimeSpan.FromHours(6) - TimeSpan.FromMicroseconds(1));
        await Customer.ReloginAsync("musteri@loca.test");
        await other.ReloginAsync("baskasi@loca.test");
        (await Customer.CancelOrderAsync(first)).ShouldBe(HttpStatusCode.OK);

        Clock.Advance(TimeSpan.FromMicroseconds(1)); // now == sınır → kapalı
        var closed = (await other.CancelOrderAsync(second)).ShouldFail(HttpStatusCode.Conflict, "cancellation_window_closed");
        Assert.Equal(Clock.Now, closed.Json["cancelDeadline"]!.GetValue<DateTimeOffset>());
        Assert.Equal("Paid", await OrderStatusAsync(second));
        Assert.Equal(0, await RefundsAsync(second));
        Assert.False((await other.OrderAsync(second))["canCancel"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Order_with_a_used_ticket_or_owned_by_someone_else_or_unpaid_cannot_be_cancelled()
    {
        await ArrangeGateAsync(startsIn: TimeSpan.FromHours(26)); // iptal penceresi açık (2 saat daha)
        var orderId = await PaidOrderAsync(null, 0, 1);
        var tickets = await TicketsWithTokensAsync(Customer);
        var (other, _) = await SignedInAsync("baskasi@loca.test");
        var (_, unpaid) = await StartCheckoutAsync("tok_success", other, 2);

        (await other.CancelOrderAsync(orderId)).ShouldFail(HttpStatusCode.NotFound, "not_found");
        (await Organizer.CancelOrderAsync(orderId)).ShouldFail(HttpStatusCode.NotFound, "not_found");
        (await other.CancelOrderAsync(unpaid)).ShouldFail(HttpStatusCode.Conflict, "order_not_paid");
        (await Customer.PostAsync($"/api/orders/{orderId}/cancel")).ShouldFail(HttpStatusCode.BadRequest, "idempotency_key_required");

        // Check-in penceresi normalde bu kadar erken açık değildir; kullanılmış bileti kısıtlara uygun biçimde kur.
        await Database.ExecuteAsAdminAsync("UPDATE tickets.tickets SET status = 'CheckedIn', checked_in_at = now() WHERE id = @id", P("id", tickets[0].Id));
        (await Customer.CancelOrderAsync(orderId)).ShouldFail(HttpStatusCode.Conflict, "order_has_checked_in_ticket");

        Assert.Equal("Paid", await OrderStatusAsync(orderId));
        Assert.Equal(0, await RefundsAsync(orderId));
        Assert.Equal("Valid", await TicketStatusAsync(tickets[1].Id));
        Assert.Equal("Sold", await SeatStatusAsync(0));
    }

    [Fact]
    public async Task Refund_response_lost_at_the_provider_still_results_in_a_single_refund()
    {
        await ArrangeAsync(Clock.Now.AddDays(10));
        var (_, orderId) = await StartCheckoutAsync("tok_refund_timeout");
        await DrainAsync();
        (await Customer.CancelOrderAsync(orderId)).ShouldBe(HttpStatusCode.OK);

        await RunOutboxAsync(); // sağlayıcı iadeyi kaydetti, bağlantı yanıt gelmeden koptu
        Assert.Equal("Pending", (await Customer.OrderAsync(orderId)).RefundStatus());
        Assert.Equal(1, await ProviderRefundsAsync());

        Clock.Advance(TimeSpan.FromSeconds(5));
        await DrainAsync();
        Assert.Equal("Succeeded", (await Customer.OrderAsync(orderId)).RefundStatus());
        Assert.Equal(1, await ProviderRefundsAsync());
        Assert.Equal(2, await CountAsync("SELECT create_attempts FROM pspsim.refunds"));
    }

    [Fact]
    public async Task Transiently_failing_refund_is_retried_with_backoff_until_it_succeeds()
    {
        await ArrangeAsync(Clock.Now.AddDays(10));
        var (_, orderId) = await StartCheckoutAsync("tok_refund_transient");
        await DrainAsync();
        (await Customer.CancelOrderAsync(orderId)).ShouldBe(HttpStatusCode.OK);

        for (var i = 0; i < 3; i++)
        {
            await DrainAsync();
            Clock.Advance(TimeSpan.FromSeconds(20));
        }
        Assert.Equal("Succeeded", (await Customer.OrderAsync(orderId)).RefundStatus());
        Assert.Equal(1, await ProviderRefundsAsync());
        Assert.Equal("3", await ScalarAsync("SELECT attempt_count::text FROM orders.refunds"));
    }

    [Fact]
    public async Task Permanently_failed_refund_keeps_the_ticket_void_stays_visible_and_is_not_reported_as_refunded()
    {
        await ArrangeAsync(Clock.Now.AddDays(10));
        var (_, orderId) = await StartCheckoutAsync("tok_refund_fail");
        await DrainAsync();
        var ticket = (await TicketsWithTokensAsync(Customer)).Single();
        (await Customer.CancelOrderAsync(orderId)).ShouldBe(HttpStatusCode.OK);
        await DrainAsync();

        var order = await Customer.OrderAsync(orderId);
        Assert.Equal("Cancelled", order.OrderStatus());
        Assert.Equal("Failed", order.RefundStatus());
        Assert.Equal("Cancelled", await TicketStatusAsync(ticket.Id)); // iade başarısız diye bilet yeniden geçerli olmaz
        Assert.Equal("Available", await SeatStatusAsync(0));
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM pspsim.refunds WHERE status = 'succeeded'"));
        Assert.Equal("refund_not_permitted", await ScalarAsync("SELECT last_error_code FROM orders.refunds"));

        var refundId = Guid.Parse((await ScalarAsync("SELECT id::text FROM orders.refunds"))!);
        var retry = (await Organizer.PostAsync($"/api/organizer/refunds/{refundId}/retry")).ShouldBe(HttpStatusCode.OK);
        Assert.Equal("Failed", retry.Json["outcome"]!.GetValue<string>()); // sağlayıcı yine reddetti: "onarıldı" denmez
        Assert.Equal("Failed", (await Customer.OrderAsync(orderId)).RefundStatus());
    }
}

/// <summary>İptal ile check-in aynı kilit kökünde yarışır: iki sıra da gerçek kilitlerle, deterministik olarak.</summary>
public sealed class CancelCheckInRaceTests(PostgresFixture postgres) : TicketTest(postgres)
{
    private async Task<(Guid OrderId, Guid TicketId, string Token)> ArrangeBothPossibleAsync()
    {
        // Seans 26 saat sonra: müşteri iptali mümkün. Check-in penceresi de açık olsun diye pencere genişletildi (bkz. Settings).
        await ArrangeGateAsync(startsIn: TimeSpan.FromHours(26));
        var orderId = await PaidOrderAsync();
        var ticket = (await TicketsWithTokensAsync(Customer)).Single();
        return (orderId, ticket.Id, ticket.Token);
    }

    protected override IDictionary<string, string?> Settings => new Dictionary<string, string?> { ["Policies:CheckInOpensBeforeMinutes"] = "1800" };

    [Fact]
    public async Task Check_in_first_then_cancel_keeps_the_admission_and_refuses_the_refund()
    {
        var (orderId, ticketId, token) = await ArrangeBothPossibleAsync();
        var gate = Failpoints.BlockOnce("checkin.before-commit");

        var checkIn = Task.Run(() => ScanAsync(Gate, token));
        await gate.Entered; // check-in kilidi tutuyor, bileti işaretledi, henüz commit etmedi
        var cancel = Task.Run(() => Customer.CancelOrderAsync(orderId));
        await LockWaits.UntilSomeoneWaitsAsync(Database, "inventory.reservations");
        Assert.False(cancel.IsCompleted);

        gate.Release();
        (await checkIn).ShouldBe(HttpStatusCode.OK);
        (await cancel).ShouldFail(HttpStatusCode.Conflict, "order_has_checked_in_ticket");

        Assert.Equal("CheckedIn", await TicketStatusAsync(ticketId));
        Assert.Equal("Paid", await OrderStatusAsync(orderId));
        Assert.Equal(0, await RefundsAsync(orderId));
        Assert.Equal("Sold", await SeatStatusAsync(0));
    }

    [Fact]
    public async Task Cancel_first_then_check_in_refunds_and_refuses_the_admission()
    {
        var (orderId, ticketId, token) = await ArrangeBothPossibleAsync();
        var gate = Failpoints.BlockOnce("order.cancel.before-commit");

        var cancel = Task.Run(() => Customer.CancelOrderAsync(orderId));
        await gate.Entered; // iptal kilidi tutuyor, bileti geçersiz kıldı, henüz commit etmedi
        var checkIn = Task.Run(() => ScanAsync(Gate, token));
        await LockWaits.UntilSomeoneWaitsAsync(Database, "inventory.reservations");
        Assert.False(checkIn.IsCompleted);

        gate.Release();
        (await cancel).ShouldBe(HttpStatusCode.OK);
        (await checkIn).ShouldFail(HttpStatusCode.Conflict, "ticket_void");

        Assert.Equal("Cancelled", await TicketStatusAsync(ticketId));
        Assert.Equal("Cancelled", await OrderStatusAsync(orderId));
        Assert.Equal(1, await RefundsAsync(orderId));
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM tickets.scan_log WHERE result = 'Accepted'"));
    }

    [Fact]
    public async Task Unsynchronized_cancel_and_check_in_never_both_succeed()
    {
        var (orderId, ticketId, token) = await ArrangeBothPossibleAsync();

        var results = await Task.WhenAll(
            Task.Run(() => Customer.CancelOrderAsync(orderId)),
            Task.Run(() => ScanAsync(Gate, token)),
            Task.Run(() => ScanAsync(Gate, token)));
        var cancelled = results[0].Status == HttpStatusCode.OK;
        var admitted = results.Skip(1).Count(r => r.Status == HttpStatusCode.OK);

        Assert.True(cancelled ^ admitted == 1, $"Exactly one of cancel/check-in must win (cancelled={cancelled}, admitted={admitted}).");
        Assert.Equal(cancelled ? "Cancelled" : "CheckedIn", await TicketStatusAsync(ticketId));
        Assert.Equal(cancelled ? 1 : 0, await RefundsAsync(orderId));
        Assert.Equal(cancelled ? "Cancelled" : "Paid", await OrderStatusAsync(orderId));
    }
}

public sealed class SessionCancellationTests(PostgresFixture postgres) : TicketTest(postgres)
{
    protected override IDictionary<string, string?> Settings => new Dictionary<string, string?>
    {
        ["Cancellation:BatchSize"] = "1", ["Cancellation:LeaseSeconds"] = "60",
    };

    [Fact]
    public async Task Cancelling_a_session_refunds_every_paid_order_voids_all_tickets_releases_holds_and_never_reopens_sales()
    {
        await ArrangeGateAsync();
        var customers = await CustomersAsync(3, "seyirci");
        var paidWithCheckIn = await PaidOrderAsync(Customer, 0, 1);
        var paid = await PaidOrderAsync(customers[0], 2);
        var holdOnly = (await customers[1].HoldSeatsAsync(Seated.SessionId, [Seated.SeatIds[3]])).HoldId();
        var (pendingHold, pendingOrder) = await StartCheckoutAsync("tok_late_success", customers[2], 4);
        await RunOutboxAsync();
        var used = (await TicketsWithTokensAsync(Customer))[0];
        (await ScanAsync(Gate, used.Token)).ShouldBe(HttpStatusCode.OK);
        var key = HoldApi.NewKey();

        var accepted = (await Organizer.CancelSessionAsync(Seated.SessionId, key)).ShouldBe(HttpStatusCode.Accepted);
        Assert.Equal("Cancelled", accepted.Json["status"]!.GetValue<string>());
        Assert.Equal("Pending", accepted.Json["jobStatus"]!.GetValue<string>());
        // 202: seans kapandı ama iadeler henüz işlenmedi.
        Assert.Equal("Paid", await OrderStatusAsync(paid));
        Assert.Equal(accepted.Body, (await Organizer.CancelSessionAsync(Seated.SessionId, key)).ShouldBe(HttpStatusCode.Accepted).Body);
        (await Organizer.CancelSessionAsync(Seated.SessionId)).ShouldFail(HttpStatusCode.Conflict, "invalid_state_transition");

        // İptal anından itibaren satış yok, check-in yok, seans public değil.
        (await customers[1].HoldSeatsAsync(Seated.SessionId, [Seated.SeatIds[9]])).ShouldFail(HttpStatusCode.Conflict, "session_cancelled");
        (await customers[1].CheckoutAsync(holdOnly)).ShouldFail(HttpStatusCode.Conflict, "session_cancelled");
        (await Factory.NewClient().GetAsync($"/api/catalog/sessions/{Seated.SessionId}")).ShouldFail(HttpStatusCode.NotFound, "not_found");
        (await Customer.CancelOrderAsync(paidWithCheckIn)).ShouldFail(HttpStatusCode.Conflict, "session_cancelled");

        Assert.Equal(4, await RunSessionCancellationAsync());
        Assert.Equal(0, await RunSessionCancellationAsync());
        await DrainAsync();

        foreach (var order in new[] { paidWithCheckIn, paid, pendingOrder })
            Assert.Equal("Cancelled,SessionCancelled", await ScalarAsync("SELECT status || ',' || cancel_reason FROM orders.orders WHERE id = @o", P("o", order)));
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM tickets.tickets WHERE status <> 'Cancelled'")); // kullanılmış bilet dahil
        Assert.Equal(2, await CountAsync("SELECT count(*) FROM orders.refunds WHERE status = 'Succeeded' AND reason = 'SessionCancellation'"));
        Assert.Equal(2, await ProviderRefundsAsync());
        Assert.Equal("Released", await HoldStatusAsync(holdOnly));
        Assert.Equal("Released", await HoldStatusAsync(pendingHold));
        // Ödenmiş siparişlerin koltukları yeniden açılmaz.
        Assert.Equal(3, await CountAsync("SELECT count(*) FROM inventory.session_seats WHERE status = 'Sold'"));
        Assert.Equal("Completed,4,2", await ScalarAsync("SELECT status || ',' || processed_count || ',' || refund_count FROM orders.session_cancellation_jobs"));

        var job = (await Organizer.GetAsync($"/api/organizer/sessions/{Seated.SessionId}/cancellation")).ShouldBe(HttpStatusCode.OK);
        Assert.Equal("Completed", job.Json["jobStatus"]!.GetValue<string>());
        // İptal edilen seans yayınlanamaz, düzenlenemez.
        (await Organizer.PostAsync($"/api/organizer/sessions/{Seated.SessionId}/publish")).ShouldFail(HttpStatusCode.Conflict, "invalid_state_transition");
        (await Organizer.PutAsync($"/api/organizer/sessions/{Seated.SessionId}/tier-prices", new { tierPrices = CatalogSetup.Tiers(("A", 1)) })).ShouldFail(HttpStatusCode.Conflict, "session_cancelled");
        var customerView = await Customer.OrderAsync(paidWithCheckIn);
        Assert.Equal("Cancelled", customerView["sessionStatus"]!.GetValue<string>());
        Assert.Equal("Succeeded", customerView.RefundStatus());
    }

    [Fact]
    public async Task Payment_that_succeeds_after_the_session_was_cancelled_is_compensated_and_never_issues_a_ticket()
    {
        await ArrangeAsync();
        var (holdId, orderId) = await StartCheckoutAsync("tok_late_success");
        await RunOutboxAsync();

        (await Organizer.CancelSessionAsync(Seated.SessionId)).ShouldBe(HttpStatusCode.Accepted);
        await RunSessionCancellationAsync();
        Assert.Equal("Cancelled", await OrderStatusAsync(orderId));
        Assert.Equal("Released", await HoldStatusAsync(holdId));
        Assert.Equal(0, await RefundsAsync(orderId)); // henüz tahsilat yok: iade de yok

        Clock.Advance(TimeSpan.FromSeconds(900));
        await Customer.ReloginAsync("musteri@loca.test");
        await DrainAsync(); // sağlayıcı geç başarıyı bildirir → telafi iadesi → iade gönderilir

        var order = await Customer.OrderAsync(orderId);
        Assert.Equal("Cancelled", order.OrderStatus());
        Assert.Equal("Succeeded", order.PaymentStatus());
        Assert.True(order["payment"]!["lateSuccess"]!.GetValue<bool>());
        Assert.Equal("Succeeded", order.RefundStatus());
        Assert.Equal("LatePaymentCompensation", order["refund"]!["reason"]!.GetValue<string>());
        Assert.Equal(1, await RefundsAsync(orderId));
        Assert.Equal(0, await TicketsAsync(orderId));
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM inventory.session_seats WHERE status = 'Sold'"));
    }

    [Fact]
    public async Task Hold_request_racing_with_session_cancellation_is_either_cancelled_by_the_job_or_rejected()
    {
        await ArrangeAsync();
        var customers = await CustomersAsync(8, "yarisan");

        var cancel = Task.Run(() => Organizer.CancelSessionAsync(Seated.SessionId));
        var holds = await Task.WhenAll(customers.Select((c, i) => Task.Run(() => c.HoldSeatsAsync(Seated.SessionId, [Seated.SeatIds[i]]))));
        (await cancel).ShouldBe(HttpStatusCode.Accepted);

        Assert.All(holds, h => Assert.True(h.Status == HttpStatusCode.Created || (h.Status == HttpStatusCode.Conflict && h.Code == "session_cancelled"), h.Text));
        while (await RunSessionCancellationAsync() > 0) { }
        // İptalden önce commit olmuş her hold iş tarafından serbest bırakıldı; iptalden sonra hiçbiri oluşamadı.
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM inventory.reservations WHERE status = 'Active'"));
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM inventory.session_seats WHERE status <> 'Available'"));
    }

    [Fact]
    public async Task Job_that_crashes_mid_batch_resumes_from_its_cursor_without_double_refunds()
    {
        await ArrangeAsync();
        var customers = await CustomersAsync(3, "seyirci");
        var orders = new List<Guid>();
        for (var i = 0; i < 3; i++) orders.Add(await PaidOrderAsync(customers[i], i));
        (await Organizer.CancelSessionAsync(Seated.SessionId)).ShouldBe(HttpStatusCode.Accepted);

        Failpoints.FailOnce("session-cancel.after-item"); // ilk sipariş commit olduktan sonra süreç "çöker"
        await Assert.ThrowsAsync<FailpointException>(RunSessionCancellationAsync);
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM orders.orders WHERE status = 'Cancelled'"));
        Assert.Equal("Pending,1,1", await ScalarAsync("SELECT status || ',' || processed_count || ',' || refund_count FROM orders.session_cancellation_jobs"));

        Assert.Equal(0, await RunSessionCancellationAsync()); // lease hâlâ çöken sahipte
        Clock.Advance(TimeSpan.FromSeconds(60));
        Assert.Equal(2, await RunSessionCancellationAsync()); // yeni sahip imleçten devam eder

        await DrainAsync();
        Assert.Equal(3, await CountAsync("SELECT count(*) FROM orders.refunds"));
        Assert.Equal(3, await ProviderRefundsAsync());
        Assert.Equal(3, await CountAsync("SELECT count(*) FROM ops.audit_log WHERE action = 'order.cancelled_by_session'"));
        Assert.Equal("Completed,3,3", await ScalarAsync("SELECT status || ',' || processed_count || ',' || refund_count FROM orders.session_cancellation_jobs"));
        Assert.All(orders, o => Assert.Equal("Cancelled", OrderStatusAsync(o).Result));
    }

    [Fact]
    public async Task Stale_job_owner_cannot_apply_changes_after_losing_its_lease()
    {
        await ArrangeAsync();
        var customers = await CustomersAsync(2, "seyirci");
        var first = await PaidOrderAsync(customers[0], 0);
        var second = await PaidOrderAsync(customers[1], 1);
        (await Organizer.CancelSessionAsync(Seated.SessionId)).ShouldBe(HttpStatusCode.Accepted);
        var reservationOf = async (Guid order) => Guid.Parse((await ScalarAsync("SELECT reservation_id::text FROM orders.orders WHERE id = @o", P("o", order)))!);

        var stale = await Factory.WithScopeAsync(sp => sp.GetRequiredService<SessionCancellationProcessor>().ClaimAsync(CancellationToken.None, "owner-a"));
        Assert.NotNull(stale);
        Clock.Advance(TimeSpan.FromSeconds(60)); // A'nın lease'i doldu
        var current = await Factory.WithScopeAsync(sp => sp.GetRequiredService<SessionCancellationProcessor>().ClaimAsync(CancellationToken.None, "owner-b"));
        Assert.NotNull(current);
        Assert.NotEqual(stale!.LeaseToken, current!.LeaseToken);

        // Eski sahip geç uyanıp bir siparişi işlemeye çalışır: transaction geri alınır, hiçbir etki kalmaz.
        var secondReservation = await reservationOf(second);
        Assert.False(await Factory.WithScopeAsync(sp => sp.GetRequiredService<SessionCancellationProcessor>().ProcessReservationAsync(stale, secondReservation, CancellationToken.None)));
        Assert.Equal("Paid", await OrderStatusAsync(second));
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM orders.refunds"));
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM tickets.tickets WHERE status = 'Cancelled'"));
        Assert.False(await Factory.WithScopeAsync(sp => sp.GetRequiredService<SessionCancellationProcessor>().CompleteAsync(stale, CancellationToken.None)));

        // Geçerli sahip normal ilerler.
        var firstReservation = await reservationOf(first);
        Assert.True(await Factory.WithScopeAsync(sp => sp.GetRequiredService<SessionCancellationProcessor>().ProcessReservationAsync(current, firstReservation, CancellationToken.None)));
        Assert.Equal("Cancelled", await OrderStatusAsync(first));
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM orders.refunds"));
    }

    [Fact]
    public async Task Only_the_owning_organizer_admin_can_cancel_a_session()
    {
        await ArrangeGateAsync();
        var (otherOrganizer, _, _) = await OrganizerAsync("org-b@loca.test", "Martı Etkinlik");

        (await otherOrganizer.CancelSessionAsync(Seated.SessionId)).ShouldFail(HttpStatusCode.NotFound, "not_found");
        (await Customer.CancelSessionAsync(Seated.SessionId)).ShouldFail(HttpStatusCode.NotFound, "not_found");
        (await Gate.CancelSessionAsync(Seated.SessionId)).ShouldFail(HttpStatusCode.Forbidden, "forbidden");
        (await otherOrganizer.GetAsync($"/api/organizer/sessions/{Seated.SessionId}/cancellation")).ShouldFail(HttpStatusCode.NotFound, "not_found");

        Assert.Equal("Published", await ScalarAsync("SELECT status FROM catalog.sessions WHERE id = @s", P("s", Seated.SessionId)));
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM orders.session_cancellation_jobs"));
    }
}

public sealed class RetentionAndShutdownTests(PostgresFixture postgres) : TicketTest(postgres)
{
    [Fact]
    public async Task Retention_removes_only_expired_technical_records_and_never_touches_business_or_audit_data()
    {
        await ArrangeGateAsync();
        var orderId = await PaidOrderAsync();
        var (_, asyncOrder) = await StartCheckoutAsync("tok_async_success", null, 1);
        await DrainAsync(); // webhook → inbox kaydı
        var ticket = (await TicketsWithTokensAsync(Customer)).First();
        (await ScanAsync(Gate, ticket.Token)).ShouldBe(HttpStatusCode.OK);
        await Database.ExecuteAsAdminAsync("UPDATE ops.outbox_messages SET dead_lettered_at = now(), processed_at = NULL WHERE id = (SELECT id FROM ops.outbox_messages ORDER BY sequence LIMIT 1)");

        var business = async () => await ScalarAsync("""
            SELECT concat_ws(',', (SELECT count(*) FROM inventory.reservations), (SELECT count(*) FROM inventory.reservation_lines), (SELECT count(*) FROM orders.orders),
                (SELECT count(*) FROM orders.payments), (SELECT count(*) FROM tickets.tickets), (SELECT count(*) FROM tickets.scan_log), (SELECT count(*) FROM ops.audit_log))
            """);
        var before = await business();
        Task<RetentionReport> PurgeAsync() => Factory.WithScopeAsync(sp => sp.GetRequiredService<RetentionProcessor>().PurgeAsync(CancellationToken.None));

        Assert.Equal(0, (await PurgeAsync()).Total); // hiçbir şeyin süresi dolmadı

        Clock.Advance(TimeSpan.FromHours(24));
        var afterDay = await PurgeAsync();
        Assert.True(afterDay.IdempotencyRecords >= 5);
        Assert.Equal(0, afterDay.InboxMessages + afterDay.OutboxMessages + afterDay.UserSessions);
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM ops.idempotency_records"));

        Clock.Advance(TimeSpan.FromDays(7));
        Assert.Equal(3, (await PurgeAsync()).OutboxMessages); // 4 mesajdan biri dead-letter yapıldı
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM ops.outbox_messages")); // dead-letter mesaj silinmez
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM ops.inbox_messages"));

        Clock.Advance(TimeSpan.FromDays(23));
        var afterMonth = await PurgeAsync();
        Assert.Equal(1, afterMonth.InboxMessages);
        Assert.True(afterMonth.UserSessions >= 3);
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM identity.user_sessions"));

        Assert.Equal(before, await business());
        Assert.Equal("Paid", await OrderStatusAsync(orderId));
        Assert.Equal("Paid", await OrderStatusAsync(asyncOrder));

        // Idempotency kaydı silindikten sonra aynı işlem yeniden gönderilse bile iş düzeyi kısıtlar çift etkiyi engeller.
        await Customer.ReloginAsync("musteri@loca.test");
        var holdId = Guid.Parse((await ScalarAsync("SELECT reservation_id::text FROM orders.orders WHERE id = @o", P("o", orderId)))!);
        (await Customer.CheckoutAsync(holdId)).ShouldFail(HttpStatusCode.Conflict, "order_already_exists");
    }

    [Fact]
    public async Task Idempotency_and_inbox_tables_have_no_foreign_keys_to_or_from_business_tables()
    {
        var links = await CountAsync("""
            SELECT count(*) FROM pg_constraint c
            WHERE c.contype = 'f' AND (
                c.conrelid IN ('ops.idempotency_records'::regclass, 'ops.inbox_messages'::regclass, 'ops.outbox_messages'::regclass) OR
                c.confrelid IN ('ops.idempotency_records'::regclass, 'ops.inbox_messages'::regclass, 'ops.outbox_messages'::regclass))
            """);
        Assert.Equal(0, links);

        // Finans ve bilet zincirindeki tüm yabancı anahtarlar silmeyi engeller (RESTRICT / NO ACTION); CASCADE yoktur.
        var cascades = await CountAsync("""
            SELECT count(*) FROM pg_constraint c JOIN pg_namespace n ON n.oid = c.connamespace
            WHERE c.contype = 'f' AND c.confdeltype = 'c' AND n.nspname IN ('orders', 'tickets', 'inventory', 'catalog')
            """);
        Assert.Equal(0, cascades);
    }

    private sealed class BlockingProcessor : IBatchProcessor
    {
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool ObservedCancellation;
        public string Name => "blocking";

        public async Task<int> RunOnceAsync(CancellationToken ct)
        {
            Started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { ObservedCancellation = true; throw; }
            return 0;
        }
    }

    [Fact]
    public async Task Worker_stops_promptly_on_shutdown_and_the_in_flight_iteration_observes_cancellation()
    {
        var processor = new BlockingProcessor();
        var services = new ServiceCollection().AddSingleton(processor).BuildServiceProvider();
        var worker = new ProcessorWorker<BlockingProcessor>(services.GetRequiredService<IServiceScopeFactory>(), Options.Create(new WorkerOptions { Enabled = true }),
            new WorkerSignal<BlockingProcessor>(), NullLogger<ProcessorWorker<BlockingProcessor>>.Instance);

        await worker.StartAsync(CancellationToken.None);
        await processor.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var stop = worker.StopAsync(CancellationToken.None);
        await stop.WaitAsync(TimeSpan.FromSeconds(10)); // asılı kalmaz

        Assert.True(processor.ObservedCancellation);
        Assert.True(worker.ExecuteTask!.IsCompletedSuccessfully);
    }
}
