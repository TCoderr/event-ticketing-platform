using System.Net;
using System.Text.Json.Nodes;
using Loca.Api.Modules.Orders;
using Loca.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Loca.IntegrationTests.Stage5;

/// <summary>Satın alma testlerinin ortak düzeneği: yayınlanmış koltuklu seans + giriş yapmış müşteri.</summary>
public abstract class PurchaseTest(PostgresFixture postgres) : IntegrationTest(postgres)
{
    protected TestClient Organizer { get; private set; } = null!;
    protected Guid OrganizationId { get; private set; }
    protected SeatedSessionSetup Seated { get; private set; } = null!;
    protected TestClient Customer { get; private set; } = null!;
    protected Guid CustomerId { get; private set; }
    protected const long SeatPrice = 45000;

    protected async Task ArrangeAsync(DateTimeOffset? startsAt = null, bool demoSimulator = true)
    {
        (Organizer, _, OrganizationId) = await OrganizerAsync("org-a@loca.test", "Kumru Sahne");
        Seated = await CatalogSetup.SeatedSessionAsync(Organizer, OrganizationId, startsAt ?? Clock.Now.AddDays(10), priceMinor: SeatPrice, demoSimulator: demoSimulator);
        (Customer, CustomerId) = await SignedInAsync("musteri@loca.test");
    }

    /// <summary>Hold + checkout; ödeme henüz başlatılmamıştır (outbox çalıştırılmadı).</summary>
    protected async Task<(Guid HoldId, Guid OrderId)> StartCheckoutAsync(string method, TestClient? customer = null, params int[] seatIndexes)
    {
        customer ??= Customer;
        var seats = (seatIndexes.Length == 0 ? [0] : seatIndexes).Select(i => Seated.SeatIds[i]);
        var holdId = (await customer.HoldSeatsAsync(Seated.SessionId, seats)).HoldId();
        var orderId = (await customer.CheckoutAsync(holdId, method)).OrderId();
        return (holdId, orderId);
    }

    protected NpgsqlParameter P(string name, object value) => new(name, value);
    protected Task<string?> PaymentColumnAsync(Guid orderId, string column) => ScalarAsync($"SELECT {column}::text FROM orders.payments WHERE order_id = @o", P("o", orderId));
    protected Task<string?> OrderStatusAsync(Guid orderId) => ScalarAsync("SELECT status FROM orders.orders WHERE id = @o", P("o", orderId));
    protected Task<string?> SeatStatusAsync(int index) => ScalarAsync("SELECT status FROM inventory.session_seats WHERE id = @id", P("id", Seated.SeatIds[index]));
    protected Task<string?> HoldStatusAsync(Guid holdId) => ScalarAsync("SELECT status FROM inventory.reservations WHERE id = @id", P("id", holdId));
    protected Task<long> ProviderChargesAsync() => CountAsync("SELECT count(*) FROM pspsim.charges WHERE status <> 'unsubmitted'");
    protected Task<long> ProviderRefundsAsync() => CountAsync("SELECT count(*) FROM pspsim.refunds WHERE status <> 'unsubmitted'");
    protected Task<long> TicketsAsync(Guid orderId) => CountAsync("SELECT count(*) FROM tickets.tickets WHERE order_id = @o", P("o", orderId));
    protected Task<long> RefundsAsync(Guid orderId) => CountAsync("SELECT count(*) FROM orders.refunds WHERE order_id = @o", P("o", orderId));

    protected Task<ApplyOutcome> ApplyAsync(Guid orderId, ProviderChargeStatus status, int sequence, long? amountMinor = null) =>
        Factory.WithScopeAsync(sp => sp.GetRequiredService<PaymentService>().ApplyChargeStateAsync(PurchaseApi.MerchantReference(orderId),
            new ProviderCharge("ch_test", status, status == ProviderChargeStatus.Declined ? "declined_in_test" : null, sequence, amountMinor ?? SeatPrice, "TRY"), "test", CancellationToken.None));
}

public sealed class CheckoutUniquenessTests(PostgresFixture postgres) : PurchaseTest(postgres)
{
    [Fact]
    public async Task Checkout_returns_202_which_means_recorded_not_paid_and_replays_byte_for_byte()
    {
        await ArrangeAsync();
        var holdId = (await Customer.HoldSeatsAsync(Seated.SessionId, [Seated.SeatIds[0], Seated.SeatIds[1]])).HoldId();
        var key = HoldApi.NewKey();

        var accepted = (await Customer.CheckoutAsync(holdId, "tok_success", key)).ShouldBe(HttpStatusCode.Accepted);
        Assert.Equal("PendingPayment", accepted.Json.OrderStatus());
        Assert.Equal("Initiated", accepted.Json.PaymentStatus());
        Assert.Equal("order_recorded_payment_not_yet_confirmed", accepted.Json["note"]!.GetValue<string>());
        Assert.Equal(90000, accepted.Json["totalMinor"]!.GetValue<long>());
        Assert.StartsWith("LC-", accepted.Json["orderNumber"]!.GetValue<string>());
        Assert.Equal($"/api/orders/{accepted.OrderId()}", accepted.Header("Location"));
        Assert.Equal(0, await ProviderChargesAsync()); // 202 anında sağlayıcıya henüz gidilmedi; tahsilat yok

        var replay = (await Customer.CheckoutAsync(holdId, "tok_success", key)).ShouldBe(HttpStatusCode.Accepted);
        Assert.Equal(accepted.Body, replay.Body);
        Assert.Equal("true", replay.Header("Idempotent-Replayed"));

        var live = await Customer.OrderAsync(accepted.OrderId());
        Assert.Equal("PendingPayment", live.OrderStatus());
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM orders.orders"));
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM orders.payments"));
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM ops.outbox_messages WHERE type = 'payment.initiate'"));
    }

    [Fact]
    public async Task Same_hold_with_a_different_key_does_not_create_a_second_order_or_payment()
    {
        await ArrangeAsync();
        var (holdId, orderId) = await StartCheckoutAsync("tok_success");

        var second = (await Customer.CheckoutAsync(holdId, "tok_success")).ShouldFail(HttpStatusCode.Conflict, "order_already_exists");
        Assert.Equal(orderId, second.Json["orderId"]!.GetValue<Guid>());
        (await Customer.CheckoutAsync(holdId, "tok_decline")).ShouldFail(HttpStatusCode.Conflict, "order_already_exists");

        await DrainAsync();
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM orders.orders"));
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM orders.payments"));
        Assert.Equal(1, await ProviderChargesAsync());
        Assert.Equal("Paid", await OrderStatusAsync(orderId));
    }

    [Fact]
    public async Task Twelve_concurrent_checkouts_with_different_keys_produce_one_order_and_one_charge()
    {
        await ArrangeAsync();
        var holdId = (await Customer.HoldSeatsAsync(Seated.SessionId, [Seated.SeatIds[0]])).HoldId();

        var responses = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => Customer.CheckoutAsync(holdId))));

        Assert.Single(responses, r => r.Status == HttpStatusCode.Accepted);
        Assert.All(responses.Where(r => r.Status != HttpStatusCode.Accepted), r => r.ShouldFail(HttpStatusCode.Conflict, "order_already_exists"));
        await DrainAsync();
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM orders.orders"));
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM orders.payments"));
        Assert.Equal(1, await ProviderChargesAsync());
        Assert.Equal(1, await TicketsAsync(responses.Single(r => r.Status == HttpStatusCode.Accepted).OrderId()));
    }

    [Fact]
    public async Task Database_itself_rejects_a_second_order_payment_or_refund_for_the_same_parent()
    {
        await ArrangeAsync();
        var (holdId, orderId) = await StartCheckoutAsync("tok_success");
        await DrainAsync();
        var paymentId = Guid.Parse((await PaymentColumnAsync(orderId, "id"))!);

        async Task Violates(string sql, string constraint)
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => Database.ExecuteAsAppAsync(sql));
            Assert.Equal("23505", error.SqlState);
            Assert.Equal(constraint, error.ConstraintName);
        }

        // Uygulama kilidi ve idempotency tamamen atlansa bile iş düzeyi tekillik veritabanında korunur.
        await Violates($"""
            INSERT INTO orders.orders (id, reservation_id, user_id, session_id, organization_id, order_number, status, total_minor, currency, created_at)
            VALUES (gen_random_uuid(), '{holdId}', '{CustomerId}', '{Seated.SessionId}', '{OrganizationId}', 'LC-DUPLICATE1', 'PendingPayment', 1, 'TRY', now())
            """, "ux_orders_reservation");
        await Violates($"""
            INSERT INTO orders.payments (id, order_id, merchant_reference, status, amount_minor, currency, method_token, last_provider_sequence, attempt_count, created_at, updated_at)
            VALUES (gen_random_uuid(), '{orderId}', 'pay_duplicate', 'Initiated', 1, 'TRY', 'tok_success', 0, 0, now(), now())
            """, "ix_payments_order_id");

        var refundSql = $"""
            INSERT INTO orders.refunds (id, payment_id, order_id, merchant_reference, reason, status, amount_minor, currency, attempt_count, created_at, updated_at)
            VALUES (gen_random_uuid(), '{paymentId}', '{orderId}', 'ref_' || gen_random_uuid(), 'CustomerCancellation', 'Pending', 45000, 'TRY', 0, now(), now())
            """;
        Assert.Equal(1, await Database.ExecuteAsAppAsync(refundSql));
        await Violates(refundSql, "ux_refunds_payment");
    }

    [Fact]
    public async Task Checkout_guards_ownership_expiry_and_payment_method()
    {
        await ArrangeAsync();
        var (other, _) = await SignedInAsync("baskasi@loca.test");
        var holdId = (await Customer.HoldSeatsAsync(Seated.SessionId, [Seated.SeatIds[0]])).HoldId();

        (await other.CheckoutAsync(holdId)).ShouldFail(HttpStatusCode.NotFound, "not_found");
        (await Customer.CheckoutAsync(holdId, "tok_free_tickets")).ShouldFail(HttpStatusCode.UnprocessableEntity, "payment_method_not_allowed");
        (await Customer.PostAsync($"/api/holds/{holdId}/checkout", """{"paymentMethod":"tok_success","amountMinor":1}""", HoldApi.NewKey()))
            .ShouldFail(HttpStatusCode.BadRequest, "unknown_field"); // fiyatı istemci belirleyemez
        (await Customer.PostAsync($"/api/holds/{holdId}/checkout", new { paymentMethod = "tok_success" })).ShouldFail(HttpStatusCode.BadRequest, "idempotency_key_required");

        Clock.Advance(TimeSpan.FromSeconds(600)); // now == expiresAt
        var expired = (await Customer.CheckoutAsync(holdId)).ShouldFail(HttpStatusCode.Conflict, "hold_expired");
        Assert.Equal(Clock.Now, expired.Json["serverTime"]!.GetValue<DateTimeOffset>());
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM orders.orders"));
    }

    [Fact]
    public async Task Demo_payment_scenarios_are_refused_unless_the_session_opted_in()
    {
        await ArrangeAsync(demoSimulator: false);
        var holdId = (await Customer.HoldSeatsAsync(Seated.SessionId, [Seated.SeatIds[0]])).HoldId();

        (await Customer.CheckoutAsync(holdId, "tok_decline")).ShouldFail(HttpStatusCode.UnprocessableEntity, "payment_method_not_allowed");
        (await Customer.CheckoutAsync(holdId, "tok_late_success")).ShouldFail(HttpStatusCode.UnprocessableEntity, "payment_method_not_allowed");
        (await Customer.CheckoutAsync(holdId, "tok_success")).ShouldBe(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task Hold_cannot_be_released_once_checkout_started_and_orders_are_private()
    {
        await ArrangeAsync();
        var (holdId, orderId) = await StartCheckoutAsync("tok_success");
        var (other, _) = await SignedInAsync("baskasi@loca.test");

        (await Customer.DeleteAsync($"/api/holds/{holdId}")).ShouldFail(HttpStatusCode.Conflict, "order_already_exists");
        (await other.GetAsync($"/api/orders/{orderId}")).ShouldFail(HttpStatusCode.NotFound, "not_found");
        Assert.Empty((await other.GetAsync("/api/orders")).ShouldBe(HttpStatusCode.OK).Json.AsArray());
        Assert.Single((await Customer.GetAsync("/api/orders")).ShouldBe(HttpStatusCode.OK).Json.AsArray());
        Assert.Equal(orderId, (await Customer.GetAsync($"/api/holds/{holdId}")).Json["orderId"]!.GetValue<Guid>());
    }
}

public sealed class PaymentOutcomeTests(PostgresFixture postgres) : PurchaseTest(postgres)
{
    [Fact]
    public async Task Success_consumes_the_hold_marks_the_order_paid_and_records_the_issuance_intent_in_one_commit()
    {
        await ArrangeAsync();
        var (holdId, orderId) = await StartCheckoutAsync("tok_success", null, 0, 1);

        Assert.Equal(1, await RunOutboxAsync()); // yalnız ödeme başlatma mesajı
        var order = await Customer.OrderAsync(orderId);
        Assert.Equal("Paid", order.OrderStatus());
        Assert.Equal("Succeeded", order.PaymentStatus());
        Assert.NotNull(order["paidAt"]);
        Assert.Equal("Consumed", await HoldStatusAsync(holdId));
        Assert.Equal("Sold", await SeatStatusAsync(0));
        Assert.Equal("Sold", await SeatStatusAsync(1));
        // Bilet üretim niyeti Paid ile aynı commit'te outbox'a yazıldı; biletler henüz üretilmedi.
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM ops.outbox_messages WHERE type = 'order.paid' AND processed_at IS NULL"));
        Assert.Equal(0, await TicketsAsync(orderId));

        await DrainAsync();
        Assert.Equal(2, await TicketsAsync(orderId));
        Assert.Equal(1, await ProviderChargesAsync());
        Assert.Equal(0, await RefundsAsync(orderId));
    }

    [Fact]
    public async Task Decline_fails_the_order_with_a_decline_reason_and_frees_the_inventory_immediately()
    {
        await ArrangeAsync();
        var (holdId, orderId) = await StartCheckoutAsync("tok_decline");
        await DrainAsync();

        var order = await Customer.OrderAsync(orderId);
        Assert.Equal("Failed", order.OrderStatus());
        Assert.Equal("Declined", order.PaymentStatus());
        Assert.Equal("Declined", order["payment"]!["failureKind"]!.GetValue<string>());
        Assert.Equal("insufficient_funds", order["payment"]!["failureCode"]!.GetValue<string>());
        Assert.Equal("Released", await HoldStatusAsync(holdId));
        Assert.Equal("Available", await SeatStatusAsync(0));
        Assert.Equal(0, await TicketsAsync(orderId));

        // Müşteri aynı koltuğu yeniden tutup yeni bir siparişle ödeyebilir.
        var (_, retryOrder) = await StartCheckoutAsync("tok_success");
        await DrainAsync();
        Assert.Equal("Paid", await OrderStatusAsync(retryOrder));
    }

    [Fact]
    public async Task Transient_provider_failure_is_unknown_not_declined_and_reconciliation_completes_the_payment()
    {
        await ArrangeAsync();
        var (holdId, orderId) = await StartCheckoutAsync("tok_transient");
        await RunOutboxAsync();

        // Zaman aşımı/5xx ret değildir: sipariş ödemeyi bekler, hold yerinde durur, ret nedeni yazılmaz.
        var pending = await Customer.OrderAsync(orderId);
        Assert.Equal("PendingPayment", pending.OrderStatus());
        Assert.Equal("Unknown", pending.PaymentStatus());
        Assert.Null(pending["payment"]!["failureKind"]);
        Assert.Equal("Active", await HoldStatusAsync(holdId));
        Assert.Equal("Held", await SeatStatusAsync(0));
        Assert.Equal(0, await ProviderChargesAsync());
        Assert.Equal(0, await RunReconciliationAsync()); // zamanı gelmeden sorgulanmaz

        Clock.Advance(TimeSpan.FromSeconds(20));
        Assert.Equal(1, await RunReconciliationAsync());
        await DrainAsync();

        Assert.Equal("Paid", await OrderStatusAsync(orderId));
        Assert.Equal(1, await ProviderChargesAsync());
        Assert.Equal(1, await TicketsAsync(orderId));
        Assert.Null(await PaymentColumnAsync(orderId, "next_reconcile_at"));
    }

    [Fact]
    public async Task When_the_provider_charged_but_the_response_was_lost_recovery_does_not_charge_twice()
    {
        await ArrangeAsync();
        var (_, orderId) = await StartCheckoutAsync("tok_timeout_success");
        await RunOutboxAsync();

        // Sağlayıcı tahsilatı kaydetti, uygulama yanıtı alamadı.
        Assert.Equal("Unknown", await PaymentColumnAsync(orderId, "status"));
        Assert.Equal("PendingPayment", await OrderStatusAsync(orderId));
        Assert.Equal(1, await ProviderChargesAsync());

        Clock.Advance(TimeSpan.FromSeconds(20));
        await RunReconciliationAsync();
        await DrainAsync();

        Assert.Equal("Paid", await OrderStatusAsync(orderId));
        Assert.Equal("Succeeded", await PaymentColumnAsync(orderId, "status"));
        Assert.Equal(1, await ProviderChargesAsync());
        Assert.Equal(1, await CountAsync("SELECT create_attempts FROM pspsim.charges")); // sorgu ile çözüldü; ikinci tahsilat isteği gitmedi
        Assert.Equal(1, await TicketsAsync(orderId));
    }

    [Fact]
    public async Task Lost_response_on_an_ordinary_charge_is_recovered_by_retrying_with_the_same_merchant_reference()
    {
        await ArrangeAsync();
        var (_, orderId) = await StartCheckoutAsync("tok_success");
        ProviderFaults.DropResponses(1);
        await RunOutboxAsync();
        Assert.Equal("Unknown", await PaymentColumnAsync(orderId, "status"));
        Assert.Equal(1, await ProviderChargesAsync());

        // Bu kez sorgu da başarısız olsun: yeniden deneme zamanı ileri alınır, durum Unknown kalır.
        Clock.Advance(TimeSpan.FromSeconds(20));
        ProviderFaults.RefuseRequests(1);
        await RunReconciliationAsync();
        Assert.Equal("Unknown", await PaymentColumnAsync(orderId, "status"));
        Assert.Equal("PendingPayment", await OrderStatusAsync(orderId));

        Clock.Advance(TimeSpan.FromSeconds(40));
        await RunReconciliationAsync();
        await DrainAsync();
        Assert.Equal("Paid", await OrderStatusAsync(orderId));
        Assert.Equal(1, await ProviderChargesAsync());
    }

    [Fact]
    public async Task Unknown_that_resolves_to_a_decline_only_then_fails_the_order_and_frees_the_inventory()
    {
        await ArrangeAsync();
        var (holdId, orderId) = await StartCheckoutAsync("tok_timeout_decline");
        await RunOutboxAsync();
        Assert.Equal("Unknown", await PaymentColumnAsync(orderId, "status"));
        Assert.Equal("Held", await SeatStatusAsync(0)); // sonuç bilinmeden envanter bırakılmaz

        Clock.Advance(TimeSpan.FromSeconds(20));
        await RunReconciliationAsync();

        Assert.Equal("Declined", await PaymentColumnAsync(orderId, "status"));
        Assert.Equal("Declined", await PaymentColumnAsync(orderId, "failure_kind"));
        Assert.Equal("Failed", await OrderStatusAsync(orderId));
        Assert.Equal("Released", await HoldStatusAsync(holdId));
        Assert.Equal("Available", await SeatStatusAsync(0));
    }

    [Fact]
    public async Task Provider_rejection_is_a_provider_error_distinct_from_a_decline()
    {
        await ArrangeAsync();
        var (holdId, orderId) = await StartCheckoutAsync("tok_success");
        ProviderFaults.RespondWith(HttpStatusCode.UnprocessableEntity, 1);
        await RunOutboxAsync();

        var order = await Customer.OrderAsync(orderId);
        Assert.Equal("Failed", order.OrderStatus());
        Assert.Equal("Failed", order.PaymentStatus());
        Assert.Equal("ProviderError", order["payment"]!["failureKind"]!.GetValue<string>());
        Assert.Equal("http_422", order["payment"]!["failureCode"]!.GetValue<string>());
        Assert.Equal("Released", await HoldStatusAsync(holdId));
        Assert.Equal(0, await ProviderChargesAsync());

        // Veritabanı, "ret" ile "sağlayıcı hatası"nın karıştırılmasını kısıtla engeller.
        var error = await Assert.ThrowsAsync<PostgresException>(() => Database.ExecuteAsAppAsync("UPDATE orders.payments SET failure_kind = 'Declined'"));
        Assert.Equal("23514", error.SqlState);
    }

    [Fact]
    public async Task Success_reported_after_a_final_decline_does_not_reopen_the_payment_but_is_flagged_for_an_operator()
    {
        await ArrangeAsync();
        var (holdId, orderId) = await StartCheckoutAsync("tok_decline");
        await DrainAsync();
        Assert.Equal("Declined", await PaymentColumnAsync(orderId, "status"));

        Assert.Equal(ApplyOutcome.AlreadyFinal, await ApplyAsync(orderId, ProviderChargeStatus.Succeeded, 5));

        // Terminal durum geri dönmez: sipariş Paid olmaz, bilet/iade otomatik üretilmez; ama görünür bir inceleme kaydı oluşur.
        Assert.Equal("Declined", await PaymentColumnAsync(orderId, "status"));
        Assert.Equal("Failed", await OrderStatusAsync(orderId));
        Assert.Equal("Released", await HoldStatusAsync(holdId));
        Assert.Equal("success_reported_after_final", await PaymentColumnAsync(orderId, "review_reason"));
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM ops.audit_log WHERE action = 'payment.success_after_final'"));
        Assert.Equal(0, await TicketsAsync(orderId));
        var queue = (await Organizer.GetAsync($"/api/organizer/organizations/{OrganizationId}/operations")).ShouldBe(HttpStatusCode.OK).Json;
        Assert.Equal(1, queue["counts"]!["reviewPayments"]!.GetValue<int>());
        Assert.Equal("success_reported_after_final", queue["items"]!.AsArray().Single()!["detail"]!.GetValue<string>());
    }

    [Fact]
    public async Task Asynchronous_success_arrives_by_signed_webhook()
    {
        await ArrangeAsync();
        var (_, orderId) = await StartCheckoutAsync("tok_async_success");
        await RunOutboxAsync();
        Assert.Equal("Pending", await PaymentColumnAsync(orderId, "status")); // 202: kabul edildi, ödenmedi
        Assert.Equal("PendingPayment", await OrderStatusAsync(orderId));

        await DrainAsync();
        Assert.Equal("Paid", await OrderStatusAsync(orderId));
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM ops.inbox_messages WHERE outcome = 'applied'"));
        Assert.Equal(1, await TicketsAsync(orderId));
    }

    [Fact]
    public async Task Duplicate_callback_has_a_single_business_effect()
    {
        await ArrangeAsync();
        var (_, orderId) = await StartCheckoutAsync("tok_async_duplicate");
        await DrainAsync();

        Assert.Equal(2, await CountAsync("SELECT count(*) FROM pspsim.webhook_deliveries WHERE delivered_at IS NOT NULL AND last_status = 200"));
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM ops.inbox_messages"));
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM ops.audit_log WHERE action = 'order.paid'"));
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM ops.outbox_messages WHERE type = 'order.paid'"));
        Assert.Equal(1, await TicketsAsync(orderId));
        Assert.Equal("Paid", await OrderStatusAsync(orderId));
    }

    [Fact]
    public async Task Out_of_order_callbacks_do_not_move_a_paid_payment_backwards()
    {
        await ArrangeAsync();
        var (_, orderId) = await StartCheckoutAsync("tok_async_out_of_order");
        await DrainAsync(); // önce charge.succeeded (sıra 2) teslim edilir
        Assert.Equal("Paid", await OrderStatusAsync(orderId));

        Clock.Advance(TimeSpan.FromSeconds(2));
        await DrainAsync(); // sonra daha eski charge.pending (sıra 1) gelir
        Assert.Equal(2, await CountAsync("SELECT count(*) FROM ops.inbox_messages"));
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM ops.inbox_messages WHERE type = 'charge.pending' AND outcome = 'ignored_stale'"));
        Assert.Equal("Succeeded", await PaymentColumnAsync(orderId, "status"));
        Assert.Equal("2", await PaymentColumnAsync(orderId, "last_provider_sequence"));
        Assert.Equal("Paid", await OrderStatusAsync(orderId));
        Assert.Equal(1, await TicketsAsync(orderId));
    }
}

/// <summary>Ödeme başarısı ile hold süre dolumu: her iki sıra, gerçek satır kilitleriyle ve deterministik olarak.</summary>
public sealed class PaymentExpiryRaceTests(PostgresFixture postgres) : PurchaseTest(postgres)
{
    private async Task<(Guid HoldId, Guid OrderId)> PendingPaymentAsync()
    {
        var result = await StartCheckoutAsync("tok_late_success");
        await RunOutboxAsync();
        Assert.Equal("Pending", await PaymentColumnAsync(result.OrderId, "status"));
        return result;
    }

    [Fact]
    public async Task Success_first_then_expiry_leaves_the_order_paid_and_the_seat_sold()
    {
        await ArrangeAsync();
        var (holdId, orderId) = await PendingPaymentAsync();

        Clock.Advance(TimeSpan.FromSeconds(600) - TimeSpan.FromMicroseconds(1)); // hold'un son geçerli anı
        Assert.Equal(ApplyOutcome.Applied, await ApplyAsync(orderId, ProviderChargeStatus.Succeeded, 2));
        Clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(0, await RunHoldExpiryAsync()); // ödenmiş hold süre dolumuyla açılmaz

        await DrainAsync();
        Assert.Equal("Paid", await OrderStatusAsync(orderId));
        Assert.Equal("Consumed", await HoldStatusAsync(holdId));
        Assert.Equal("Sold", await SeatStatusAsync(0));
        Assert.Equal(1, await TicketsAsync(orderId));
        Assert.Equal(0, await RefundsAsync(orderId));
        Assert.Null(await PaymentColumnAsync(orderId, "late_success_at"));
    }

    [Fact]
    public async Task Expiry_first_then_success_never_regains_the_seat_issues_no_ticket_and_records_exactly_one_refund()
    {
        await ArrangeAsync();
        var (holdId, orderId) = await PendingPaymentAsync();
        var (other, _) = await SignedInAsync("baskasi@loca.test");

        Clock.Advance(TimeSpan.FromSeconds(600));
        Assert.Equal(1, await RunHoldExpiryAsync());
        // Hold süresi doldu: sipariş kapanır, hold uzamaz; ödeme sonucu hâlâ bekleniyor.
        Assert.Equal("Expired", await OrderStatusAsync(orderId));
        Assert.Equal("Pending", await PaymentColumnAsync(orderId, "status"));
        var otherHold = (await other.HoldSeatsAsync(Seated.SessionId, [Seated.SeatIds[0]])).HoldId();

        // Geç başarı, aynı olay iki kez uygulansa bile tek telafi üretir.
        Assert.Equal(ApplyOutcome.Applied, await ApplyAsync(orderId, ProviderChargeStatus.Succeeded, 2));
        Assert.Equal(ApplyOutcome.Stale, await ApplyAsync(orderId, ProviderChargeStatus.Succeeded, 2));
        Assert.Equal(ApplyOutcome.AlreadyFinal, await ApplyAsync(orderId, ProviderChargeStatus.Succeeded, 3));

        var order = await Customer.OrderAsync(orderId);
        Assert.Equal("Expired", order.OrderStatus()); // yeniden Paid olmaz
        Assert.Equal("Succeeded", order.PaymentStatus());
        Assert.True(order["payment"]!["lateSuccess"]!.GetValue<bool>());
        Assert.Equal("Pending", order.RefundStatus());
        Assert.Equal("LatePaymentCompensation", order["refund"]!["reason"]!.GetValue<string>());
        Assert.Equal(1, await RefundsAsync(orderId));
        Assert.Equal(otherHold, Guid.Parse((await ScalarAsync("SELECT hold_reservation_id::text FROM inventory.session_seats WHERE id = @id", P("id", Seated.SeatIds[0])))!));
        Assert.Equal("Expired", await HoldStatusAsync(holdId));

        // Sağlayıcı tarafında tahsilatın sonuçlanması testte elle uygulandı; iadenin gönderilebilmesi için simülatörü de sonuçlandır.
        await Database.ExecuteAsAdminAsync("UPDATE pspsim.charges SET status = 'succeeded', resolve_at = NULL");
        await DrainAsync();
        Assert.Equal("Succeeded", (await Customer.OrderAsync(orderId)).RefundStatus());
        Assert.Equal(1, await ProviderRefundsAsync());
        Assert.Equal(0, await TicketsAsync(orderId));
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM ops.audit_log WHERE action = 'payment.late_success_compensated'"));
    }

    [Fact]
    public async Task While_the_payment_transaction_holds_the_aggregate_lock_the_expiry_worker_cannot_interleave()
    {
        await ArrangeAsync();
        var (holdId, orderId) = await PendingPaymentAsync();
        Clock.Advance(TimeSpan.FromSeconds(599));
        var gate = Failpoints.BlockOnce("payment.apply.after-lock");

        var apply = Task.Run(() => ApplyAsync(orderId, ProviderChargeStatus.Succeeded, 2));
        await gate.Entered; // ödeme transaction'ı kilidi aldı ve hold'un geçerli olduğunu (now < expiresAt) gördü

        Clock.Advance(TimeSpan.FromSeconds(5)); // bu arada süre doluyor
        Assert.Equal(0, await RunHoldExpiryAsync()); // satır kilitli: worker atlar, envanteri açamaz
        Assert.Equal("Held", await SeatStatusAsync(0));

        gate.Release();
        Assert.Equal(ApplyOutcome.Applied, await apply);
        Assert.Equal(0, await RunHoldExpiryAsync());
        Assert.Equal("Paid", await OrderStatusAsync(orderId));
        Assert.Equal("Consumed", await HoldStatusAsync(holdId));
        Assert.Equal("Sold", await SeatStatusAsync(0));
        Assert.Equal(0, await RefundsAsync(orderId));
    }

    [Fact]
    public async Task While_the_expiry_worker_holds_the_aggregate_lock_a_concurrent_success_waits_and_then_compensates()
    {
        await ArrangeAsync();
        var (holdId, orderId) = await PendingPaymentAsync();
        Clock.Advance(TimeSpan.FromSeconds(600));
        var gate = Failpoints.BlockOnce("hold-expiry.after-lock");

        var expiry = Task.Run(RunHoldExpiryAsync);
        await gate.Entered; // worker rezervasyon satırını kilitledi, henüz commit etmedi

        var apply = Task.Run(() => ApplyAsync(orderId, ProviderChargeStatus.Succeeded, 2));
        await LockWaits.UntilSomeoneWaitsAsync(Database, "inventory.reservations"); // ödeme aynı kilitte bekliyor
        Assert.False(apply.IsCompleted);

        gate.Release();
        Assert.Equal(1, await expiry);
        Assert.Equal(ApplyOutcome.Applied, await apply);

        Assert.Equal("Expired", await OrderStatusAsync(orderId));
        Assert.Equal("Expired", await HoldStatusAsync(holdId));
        Assert.Equal("Available", await SeatStatusAsync(0));
        Assert.Equal("Succeeded", await PaymentColumnAsync(orderId, "status"));
        Assert.NotNull(await PaymentColumnAsync(orderId, "late_success_at"));
        Assert.Equal(1, await RefundsAsync(orderId));
        await DrainAsync();
        Assert.Equal(0, await TicketsAsync(orderId));
    }

    [Fact]
    public async Task Success_after_expiry_compensates_even_if_the_worker_never_ran_and_the_seat_was_taken_over()
    {
        await ArrangeAsync();
        var (holdId, orderId) = await StartCheckoutAsync("tok_late_success", null, 0, 1);
        await RunOutboxAsync();
        var (other, _) = await SignedInAsync("baskasi@loca.test");

        Clock.Advance(TimeSpan.FromSeconds(600)); // worker hiç çalışmadı
        var otherHold = (await other.HoldSeatsAsync(Seated.SessionId, [Seated.SeatIds[1]])).HoldId(); // iki koltuktan biri devralındı

        Assert.Equal(ApplyOutcome.Applied, await ApplyAsync(orderId, ProviderChargeStatus.Succeeded, 2, amountMinor: SeatPrice * 2));

        Assert.Equal("Expired", await OrderStatusAsync(orderId));
        Assert.Equal("Expired", await HoldStatusAsync(holdId));
        Assert.Equal("Available", await SeatStatusAsync(0)); // devralınmamış koltuk serbest kaldı
        Assert.Equal("Held", await SeatStatusAsync(1));       // devralınan koltuğa dokunulmadı
        Assert.Equal(otherHold, Guid.Parse((await ScalarAsync("SELECT hold_reservation_id::text FROM inventory.session_seats WHERE id = @id", P("id", Seated.SeatIds[1])))!));
        Assert.Equal(1, await RefundsAsync(orderId));
        Assert.Equal(90000, await CountAsync("SELECT amount_minor FROM orders.refunds WHERE order_id = @o", P("o", orderId)));
        Assert.Equal(0, await RunHoldExpiryAsync());
    }
}
