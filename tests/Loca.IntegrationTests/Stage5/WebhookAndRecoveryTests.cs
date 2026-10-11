using System.Net;
using System.Text;
using Loca.Api.Infrastructure.Outbox;
using Loca.Api.Infrastructure.Persistence;
using Loca.Api.Infrastructure.Time;
using Loca.Api.Modules.Orders;
using Loca.Domain;
using Loca.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Loca.IntegrationTests.Stage5;

public sealed class WebhookTests(PostgresFixture postgres) : PurchaseTest(postgres)
{
    protected override IDictionary<string, string?> Settings => new Dictionary<string, string?>
    {
        // Rotasyon: iki secret aynı anda aktiftir.
        ["Psp:WebhookSecrets:1"] = LocaFactory.PreviousWebhookSecret,
    };

    private TestClient _anonymous = null!;

    private async Task<Guid> PendingOrderAsync()
    {
        await ArrangeAsync();
        _anonymous = Factory.NewClient();
        var (_, orderId) = await StartCheckoutAsync("tok_late_success");
        await RunOutboxAsync();
        Assert.Equal("Pending", await PaymentColumnAsync(orderId, "status"));
        return orderId;
    }

    private Task<long> InboxAsync() => CountAsync("SELECT count(*) FROM ops.inbox_messages");

    [Fact]
    public async Task Valid_signed_event_is_applied_without_any_cookie_or_csrf_token()
    {
        var orderId = await PendingOrderAsync();
        var body = WebhookSender.Event("evt_ok_1", "charge.succeeded", orderId, SeatPrice, sequence: 2);

        var response = (await WebhookSender.SendAsync(_anonymous, body, Clock.Now)).ShouldBe(HttpStatusCode.OK);

        Assert.Equal("applied", response.Json["outcome"]!.GetValue<string>());
        Assert.Equal("Paid", await OrderStatusAsync(orderId));
        Assert.Empty(_anonymous.Cookies);
    }

    [Fact]
    public async Task Forged_signature_is_rejected_and_nothing_is_recorded()
    {
        var orderId = await PendingOrderAsync();
        var body = WebhookSender.Event("evt_forged", "charge.succeeded", orderId, SeatPrice, sequence: 2);

        (await WebhookSender.SendAsync(_anonymous, body, Clock.Now, secret: "attacker-secret-0123456789")).ShouldFail(HttpStatusCode.Unauthorized, "webhook_signature_invalid");
        (await WebhookSender.SendAsync(_anonymous, body, Clock.Now, signatureOverride: $"t={Clock.Now.ToUnixTimeSeconds()},v1={new string('0', 64)}"))
            .ShouldFail(HttpStatusCode.Unauthorized, "webhook_signature_invalid");
        (await WebhookSender.SendAsync(_anonymous, body, Clock.Now, secret: null)).ShouldFail(HttpStatusCode.BadRequest, "webhook_malformed");
        (await WebhookSender.SendAsync(_anonymous, body, Clock.Now, signatureOverride: "v1=nothex")).ShouldFail(HttpStatusCode.BadRequest, "webhook_malformed");

        Assert.Equal(0, await InboxAsync());
        Assert.Equal("Pending", await PaymentColumnAsync(orderId, "status"));
        Assert.Equal("PendingPayment", await OrderStatusAsync(orderId));
    }

    [Fact]
    public async Task Signature_covers_the_raw_bytes_so_a_reserialized_body_fails()
    {
        var orderId = await PendingOrderAsync();
        var signedBody = WebhookSender.Event("evt_raw", "charge.succeeded", orderId, SeatPrice, sequence: 2);
        var signature = WebhookSignature.Sign(Encoding.UTF8.GetBytes(LocaFactory.WebhookSecret), Clock.Now.ToUnixTimeSeconds(), signedBody);

        // Aynı JSON, farklı baytlar (boşluk eklenmiş): anlam aynı olsa da imza tutmaz.
        var reserialized = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(signedBody).Replace("\"type\":", "\"type\": "));
        (await WebhookSender.SendAsync(_anonymous, reserialized, Clock.Now, signatureOverride: signature)).ShouldFail(HttpStatusCode.Unauthorized, "webhook_signature_invalid");
        // Tutarı değiştirilmiş gövde de aynı imzayla geçemez.
        var tampered = WebhookSender.Event("evt_raw", "charge.succeeded", orderId, 1, sequence: 2);
        (await WebhookSender.SendAsync(_anonymous, tampered, Clock.Now, signatureOverride: signature)).ShouldFail(HttpStatusCode.Unauthorized, "webhook_signature_invalid");

        (await WebhookSender.SendAsync(_anonymous, signedBody, Clock.Now, signatureOverride: signature)).ShouldBe(HttpStatusCode.OK);
        Assert.Equal("Paid", await OrderStatusAsync(orderId));
    }

    [Theory]
    [InlineData(-301)]
    [InlineData(301)]
    public async Task Correctly_signed_event_outside_the_replay_window_is_rejected(int offsetSeconds)
    {
        var orderId = await PendingOrderAsync();
        var body = WebhookSender.Event("evt_old", "charge.succeeded", orderId, SeatPrice, sequence: 2);

        (await WebhookSender.SendAsync(_anonymous, body, Clock.Now.AddSeconds(offsetSeconds))).ShouldFail(HttpStatusCode.Unauthorized, "webhook_timestamp_out_of_range");

        Assert.Equal(0, await InboxAsync());
        Assert.Equal("Pending", await PaymentColumnAsync(orderId, "status"));
        (await WebhookSender.SendAsync(_anonymous, body, Clock.Now.AddSeconds(offsetSeconds < 0 ? -300 : 300))).ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Same_event_id_with_the_same_payload_is_a_no_op_and_with_a_different_payload_is_a_conflict()
    {
        var orderId = await PendingOrderAsync();
        var body = WebhookSender.Event("evt_dup", "charge.succeeded", orderId, SeatPrice, sequence: 2);

        Assert.Equal("applied", (await WebhookSender.SendAsync(_anonymous, body, Clock.Now)).ShouldBe(HttpStatusCode.OK).Json["outcome"]!.GetValue<string>());
        Clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal("duplicate", (await WebhookSender.SendAsync(_anonymous, body, Clock.Now)).ShouldBe(HttpStatusCode.OK).Json["outcome"]!.GetValue<string>());

        // Aynı kimlik, farklı içerik (ret): uygulanmaz, çakışma olarak kaydedilir.
        var conflicting = WebhookSender.Event("evt_dup", "charge.declined", orderId, SeatPrice, sequence: 3, declineCode: "x");
        (await WebhookSender.SendAsync(_anonymous, conflicting, Clock.Now)).ShouldFail(HttpStatusCode.Conflict, "webhook_event_conflict");

        Assert.Equal(1, await InboxAsync());
        Assert.Equal("Succeeded", await PaymentColumnAsync(orderId, "status"));
        Assert.Equal("Paid", await OrderStatusAsync(orderId));
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM ops.audit_log WHERE action = 'order.paid'"));
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM ops.audit_log WHERE action = 'webhook.event_conflict'"));
        await DrainAsync();
        Assert.Equal(1, await TicketsAsync(orderId));
    }

    [Fact]
    public async Task Either_active_secret_verifies_during_rotation()
    {
        var orderId = await PendingOrderAsync();
        var body = WebhookSender.Event("evt_rot", "charge.succeeded", orderId, SeatPrice, sequence: 2);

        (await WebhookSender.SendAsync(_anonymous, body, Clock.Now, secret: LocaFactory.PreviousWebhookSecret)).ShouldBe(HttpStatusCode.OK);
        Assert.Equal("Paid", await OrderStatusAsync(orderId));
    }

    [Fact]
    public async Task Amount_or_currency_mismatch_is_not_applied_and_is_flagged_for_review()
    {
        var orderId = await PendingOrderAsync();

        var wrongAmount = WebhookSender.Event("evt_amount", "charge.succeeded", orderId, SeatPrice - 1, sequence: 2);
        Assert.Equal("mismatch", (await WebhookSender.SendAsync(_anonymous, wrongAmount, Clock.Now)).ShouldBe(HttpStatusCode.OK).Json["outcome"]!.GetValue<string>());
        var wrongCurrency = WebhookSender.Event("evt_currency", "charge.succeeded", orderId, SeatPrice, sequence: 2, currency: "USD");
        Assert.Equal("mismatch", (await WebhookSender.SendAsync(_anonymous, wrongCurrency, Clock.Now)).ShouldBe(HttpStatusCode.OK).Json["outcome"]!.GetValue<string>());

        Assert.Equal("Pending", await PaymentColumnAsync(orderId, "status"));
        Assert.Equal("amount_mismatch", await PaymentColumnAsync(orderId, "review_reason"));
        Assert.Equal("PendingPayment", await OrderStatusAsync(orderId));
        Assert.Equal("Held", await SeatStatusAsync(0));
    }

    [Fact]
    public async Task Unknown_reference_unknown_type_malformed_and_oversized_bodies_are_handled_safely()
    {
        var orderId = await PendingOrderAsync();

        var unknownReference = WebhookSender.Event("evt_unknown_ref", "charge.succeeded", Guid.NewGuid(), SeatPrice, sequence: 2);
        Assert.Equal("ignored_unknown_reference", (await WebhookSender.SendAsync(_anonymous, unknownReference, Clock.Now)).ShouldBe(HttpStatusCode.OK).Json["outcome"]!.GetValue<string>());
        var unknownType = WebhookSender.Event("evt_unknown_type", "charge.chargeback", orderId, SeatPrice, sequence: 2);
        Assert.Equal("ignored_unknown_type", (await WebhookSender.SendAsync(_anonymous, unknownType, Clock.Now)).ShouldBe(HttpStatusCode.OK).Json["outcome"]!.GetValue<string>());

        (await WebhookSender.SendAsync(_anonymous, "{\"id\":\"evt_incomplete\"}"u8.ToArray(), Clock.Now)).ShouldFail(HttpStatusCode.BadRequest, "webhook_malformed");
        (await WebhookSender.SendAsync(_anonymous, "not json at all"u8.ToArray(), Clock.Now)).ShouldFail(HttpStatusCode.BadRequest, "webhook_malformed");
        var oversized = Encoding.UTF8.GetBytes("{\"id\":\"" + new string('x', 17 * 1024) + "\"}");
        (await WebhookSender.SendAsync(_anonymous, oversized, Clock.Now)).ShouldFail(HttpStatusCode.RequestEntityTooLarge, "payload_too_large");

        Assert.Equal("Pending", await PaymentColumnAsync(orderId, "status"));
        Assert.Equal(2, await InboxAsync()); // yalnız imzası ve biçimi geçerli iki olay kaydedildi
    }
}

public sealed class ReconciliationAndRefundTests(PostgresFixture postgres) : PurchaseTest(postgres)
{
    protected override IDictionary<string, string?> Settings => new Dictionary<string, string?> { ["Outbox:MaxAttempts"] = "3" };

    private async Task<(Guid OrderId, Guid PaymentId)> UnknownPaymentAsync()
    {
        await ArrangeAsync();
        var (_, orderId) = await StartCheckoutAsync("tok_timeout_success");
        await RunOutboxAsync();
        Assert.Equal("Unknown", await PaymentColumnAsync(orderId, "status"));
        return (orderId, Guid.Parse((await PaymentColumnAsync(orderId, "id"))!));
    }

    [Fact]
    public async Task Operator_reconcile_reports_planned_attempted_and_succeeded_separately()
    {
        var (orderId, paymentId) = await UnknownPaymentAsync();

        var run = (await Organizer.PostAsync($"/api/organizer/payments/{paymentId}/reconcile")).ShouldBe(HttpStatusCode.OK);

        Assert.True(run.Json["planned"]!.GetValue<bool>());
        Assert.True(run.Json["attempted"]!.GetValue<bool>());
        Assert.Equal("Succeeded", run.Json["outcome"]!.GetValue<string>());
        Assert.Equal("resolved_succeeded", run.Json["detailCode"]!.GetValue<string>());
        Assert.Equal("Paid", await OrderStatusAsync(orderId));
        Assert.Equal(1, await ProviderChargesAsync());
        Assert.Equal("Succeeded", await ScalarAsync("SELECT outcome FROM orders.reconciliation_runs"));

        // Zaten sonuçlanmış ödeme: yeni deneme yapılmaz ve "başarılı onarım" sayılmaz.
        var again = (await Organizer.PostAsync($"/api/organizer/payments/{paymentId}/reconcile")).ShouldBe(HttpStatusCode.OK);
        Assert.False(again.Json["attempted"]!.GetValue<bool>());
        Assert.Equal("Planned", again.Json["outcome"]!.GetValue<string>());
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM orders.reconciliation_runs WHERE outcome = 'Succeeded'"));
    }

    [Fact]
    public async Task Failed_reconcile_attempt_is_reported_as_failed_and_changes_nothing()
    {
        var (orderId, paymentId) = await UnknownPaymentAsync();
        ProviderFaults.RefuseRequests(1);

        var run = (await Organizer.PostAsync($"/api/organizer/payments/{paymentId}/reconcile")).ShouldBe(HttpStatusCode.OK);

        Assert.True(run.Json["attempted"]!.GetValue<bool>());
        Assert.Equal("Failed", run.Json["outcome"]!.GetValue<string>());
        Assert.StartsWith("provider_unavailable", run.Json["detailCode"]!.GetValue<string>());
        Assert.Equal("Unknown", await PaymentColumnAsync(orderId, "status"));
        Assert.Equal("PendingPayment", await OrderStatusAsync(orderId));
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM orders.reconciliation_runs WHERE outcome = 'Succeeded'"));
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM orders.reconciliation_runs WHERE outcome = 'Failed'"));
    }

    [Fact]
    public async Task Reconcile_of_a_still_pending_payment_is_attempted_but_not_succeeded()
    {
        await ArrangeAsync();
        var (_, orderId) = await StartCheckoutAsync("tok_late_success");
        await RunOutboxAsync();
        var paymentId = Guid.Parse((await PaymentColumnAsync(orderId, "id"))!);

        var run = (await Organizer.PostAsync($"/api/organizer/payments/{paymentId}/reconcile")).ShouldBe(HttpStatusCode.OK);

        Assert.Equal("Attempted", run.Json["outcome"]!.GetValue<string>());
        Assert.Equal("still_pending", run.Json["detailCode"]!.GetValue<string>());
        Assert.Equal("Pending", await PaymentColumnAsync(orderId, "status"));
    }

    [Fact]
    public async Task Only_the_owning_organizer_or_a_platform_admin_can_run_recovery()
    {
        var (_, paymentId) = await UnknownPaymentAsync();
        var (otherOrganizer, _, _) = await OrganizerAsync("org-b@loca.test", "Martı Etkinlik");

        (await otherOrganizer.PostAsync($"/api/organizer/payments/{paymentId}/reconcile")).ShouldFail(HttpStatusCode.NotFound, "not_found");
        (await Customer.PostAsync($"/api/organizer/payments/{paymentId}/reconcile")).ShouldFail(HttpStatusCode.NotFound, "not_found");
        (await Organizer.PostAsync($"/api/organizer/payments/{Guid.NewGuid()}/reconcile")).ShouldFail(HttpStatusCode.NotFound, "not_found");
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM orders.reconciliation_runs"));

        var admin = await SignedInPlatformAdminAsync();
        (await admin.PostAsync($"/api/organizer/payments/{paymentId}/reconcile")).ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Reconciliation_worker_leases_a_payment_so_two_workers_do_not_process_it_at_once_and_gives_up_after_the_budget()
    {
        var (orderId, _) = await UnknownPaymentAsync();
        Clock.Advance(TimeSpan.FromSeconds(20));

        async Task<int> ClaimAsync() => await Factory.WithScopeAsync(async sp => (await sp.GetRequiredService<PaymentReconciliationProcessor>().ClaimAsync(10, CancellationToken.None)).Count);
        Assert.Equal(1, await ClaimAsync());
        Assert.Equal(0, await ClaimAsync()); // lease süresince ikinci sahip alamaz
        Clock.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(1, await ClaimAsync()); // lease doldu: çöken sahibin işi devralınır

        // Sağlayıcıya hiç ulaşılamazsa otomatik denemeler bütçe kadar sürer, sonra operatöre kalır.
        Clock.Advance(TimeSpan.FromSeconds(31));
        for (var i = 0; i < 12; i++)
        {
            ProviderFaults.RefuseRequests(1);
            await RunReconciliationAsync();
            Clock.Advance(TimeSpan.FromMinutes(6));
        }
        Assert.Null(await PaymentColumnAsync(orderId, "next_reconcile_at"));
        Assert.Equal("Unknown", await PaymentColumnAsync(orderId, "status"));
        Assert.Equal("8", await PaymentColumnAsync(orderId, "attempt_count"));
    }

    private async Task<(Guid OrderId, Guid RefundId)> LateSuccessRefundAsync(string method = "tok_late_success")
    {
        await ArrangeAsync();
        var (_, orderId) = await StartCheckoutAsync(method);
        await RunOutboxAsync();
        Clock.Advance(TimeSpan.FromSeconds(600));
        await RunHoldExpiryAsync();
        Clock.Advance(TimeSpan.FromSeconds(300)); // simülatörün geç sonuçlandırma anı (900 sn)
        await RunPspSimulatorAsync();             // sağlayıcı başarıyı webhook ile bildirir → geç başarı
        Assert.Equal("Expired", await OrderStatusAsync(orderId));
        var refundId = Guid.Parse((await ScalarAsync("SELECT id::text FROM orders.refunds WHERE order_id = @o", P("o", orderId)))!);
        return (orderId, refundId);
    }

    [Fact]
    public async Task Late_success_delivered_by_the_provider_is_refunded_once()
    {
        var (orderId, _) = await LateSuccessRefundAsync();
        Assert.Equal("Pending", await ScalarAsync("SELECT status FROM orders.refunds"));

        await DrainAsync();

        Assert.Equal("Succeeded", await ScalarAsync("SELECT status FROM orders.refunds"));
        Assert.Equal(1, await ProviderRefundsAsync());
        Assert.Equal(1, await RefundsAsync(orderId));
        Assert.Equal(0, await TicketsAsync(orderId));
        Assert.Equal("Available", await SeatStatusAsync(0));
    }

    [Fact]
    public async Task Refund_whose_provider_response_was_lost_is_retried_with_the_same_reference_and_refunds_once()
    {
        await LateSuccessRefundAsync();
        ProviderFaults.DropResponses(1); // sağlayıcı iadeyi kaydeder, yanıt kaybolur

        await RunOutboxAsync();
        Assert.Equal("Pending", await ScalarAsync("SELECT status FROM orders.refunds")); // sonuç bilinmiyor: başarı sayılmaz
        Assert.Equal(1, await ProviderRefundsAsync());
        Assert.Equal(0, await RunOutboxAsync()); // geri çekilme süresi dolmadan yeniden denenmez

        Clock.Advance(TimeSpan.FromSeconds(5));
        await DrainAsync();

        Assert.Equal("Succeeded", await ScalarAsync("SELECT status FROM orders.refunds"));
        Assert.Equal(1, await ProviderRefundsAsync());
        Assert.Equal(2, await CountAsync("SELECT create_attempts FROM pspsim.refunds"));
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM ops.audit_log WHERE action = 'refund.succeeded'"));
    }

    [Fact]
    public async Task Refund_that_exhausts_its_retry_budget_is_dead_lettered_marked_failed_and_can_be_retried_by_an_operator()
    {
        var (_, refundId) = await LateSuccessRefundAsync();

        for (var attempt = 0; attempt < 3; attempt++)
        {
            ProviderFaults.RefuseRequests(1);
            await RunOutboxAsync();
            Clock.Advance(TimeSpan.FromSeconds(30));
        }

        Assert.Equal("Failed", await ScalarAsync("SELECT status FROM orders.refunds"));
        Assert.Equal("dispatch_exhausted", await ScalarAsync("SELECT last_error_code FROM orders.refunds"));
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM ops.outbox_messages WHERE type = 'refund.requested' AND dead_lettered_at IS NOT NULL"));
        Assert.Equal(0, await ProviderRefundsAsync()); // para iadesi yapılmadı; "başarılı" olarak görünmez
        Assert.Equal(0, await RunOutboxAsync());       // dead-letter mesaj yeniden alınmaz

        // Operatör denemesi sağlayıcıya yine ulaşamazsa sonuç "Failed" olur.
        ProviderFaults.RefuseRequests(1);
        var failed = (await Organizer.PostAsync($"/api/organizer/refunds/{refundId}/retry")).ShouldBe(HttpStatusCode.OK);
        Assert.Equal("Failed", failed.Json["outcome"]!.GetValue<string>());
        Assert.Equal("Failed", await ScalarAsync("SELECT status FROM orders.refunds"));

        var succeeded = (await Organizer.PostAsync($"/api/organizer/refunds/{refundId}/retry")).ShouldBe(HttpStatusCode.OK);
        Assert.Equal("Succeeded", succeeded.Json["outcome"]!.GetValue<string>());
        Assert.Equal("Succeeded", await ScalarAsync("SELECT status FROM orders.refunds"));
        Assert.Equal(1, await ProviderRefundsAsync());
        (await Organizer.PostAsync($"/api/organizer/refunds/{refundId}/retry")).ShouldFail(HttpStatusCode.Conflict, "refund_not_retryable");
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM orders.reconciliation_runs WHERE target_type = 'refund' AND outcome = 'Succeeded'"));
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM orders.reconciliation_runs WHERE target_type = 'refund' AND outcome = 'Failed'"));
    }
}

/// <summary>Outbox dispatcher: lease, fencing, geri çekilme, zehirli mesaj ve çökme sonrası devralma.</summary>
public sealed class OutboxWorkerTests(PostgresFixture postgres) : IntegrationTest(postgres)
{
    private readonly ProbeState _probe = new();

    protected override IDictionary<string, string?> Settings => new Dictionary<string, string?>
    {
        ["Outbox:MaxAttempts"] = "3", ["Outbox:LeaseSeconds"] = "30", ["Outbox:BaseDelayMs"] = "2000",
    };

    protected override void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton(_probe);
        services.AddScoped<IOutboxHandler, ProbeHandler>();
    }

    private sealed class ProbeState
    {
        public int Handled;
        public int DeadLettered;
        public int FailuresRemaining;
    }

    private sealed class ProbeHandler(ProbeState state) : IOutboxHandler
    {
        public string Type => "test.probe";

        public Task HandleAsync(OutboxMessage message, CancellationToken ct)
        {
            if (Interlocked.Decrement(ref state.FailuresRemaining) >= 0) throw new InvalidOperationException("probe failure");
            Interlocked.Increment(ref state.Handled);
            return Task.CompletedTask;
        }

        public Task OnDeadLetteredAsync(OutboxMessage message, CancellationToken ct)
        {
            Interlocked.Increment(ref state.DeadLettered);
            return Task.CompletedTask;
        }
    }

    private Task<Guid> EnqueueAsync(string type = "test.probe") => Factory.WithScopeAsync(async sp =>
    {
        var db = sp.GetRequiredService<AppDbContext>();
        sp.GetRequiredService<IOutbox>().Enqueue(await sp.GetRequiredService<IClock>().NowAsync(), type, new { n = 1 });
        await db.SaveChangesAsync();
        return await db.OutboxMessages.OrderByDescending(m => m.Sequence).Select(m => m.Id).FirstAsync();
    });

    private Task<T> WithOutboxAsync<T>(Func<OutboxProcessor, Task<T>> work) => Factory.WithScopeAsync(sp => work(sp.GetRequiredService<OutboxProcessor>()));
    private Task<string?> ColumnAsync(Guid id, string column) => ScalarAsync($"SELECT {column}::text FROM ops.outbox_messages WHERE id = @id", new Npgsql.NpgsqlParameter("id", id));

    [Fact]
    public async Task Message_is_processed_once_and_not_picked_again()
    {
        var id = await EnqueueAsync();
        Assert.Equal(1, await RunOutboxAsync());
        Assert.Equal(0, await RunOutboxAsync());
        Assert.Equal(1, _probe.Handled);
        Assert.NotNull(await ColumnAsync(id, "processed_at"));
        Assert.Null(await ColumnAsync(id, "lease_until"));
    }

    [Fact]
    public async Task Stale_owner_cannot_overwrite_the_result_of_the_new_lease_owner()
    {
        var id = await EnqueueAsync();
        var first = Assert.Single(await WithOutboxAsync(o => o.ClaimAsync(10, CancellationToken.None, "owner-a")));
        Assert.Empty(await WithOutboxAsync(o => o.ClaimAsync(10, CancellationToken.None, "owner-b"))); // lease geçerliyken alınamaz

        Clock.Advance(TimeSpan.FromSeconds(30)); // A'nın lease'i doldu (çöktü ya da takıldı)
        var second = Assert.Single(await WithOutboxAsync(o => o.ClaimAsync(10, CancellationToken.None, "owner-b")));
        Assert.Equal(id, second.Id);
        Assert.NotEqual(first.LeaseToken, second.LeaseToken);
        Assert.Equal(2, second.AttemptCount);

        Assert.True(await WithOutboxAsync(o => o.CompleteAsync(second, CancellationToken.None)));

        // A geç uyanır: ne tamamlayabilir, ne erteleyebilir, ne de dead-letter'a alabilir.
        Assert.False(await WithOutboxAsync(o => o.CompleteAsync(first, CancellationToken.None)));
        Assert.False(await WithOutboxAsync(o => o.RescheduleAsync(first, "late", CancellationToken.None)));
        Assert.False(await WithOutboxAsync(o => o.DeadLetterAsync(first, "late", CancellationToken.None)));
        Assert.NotNull(await ColumnAsync(id, "processed_at"));
        Assert.Null(await ColumnAsync(id, "dead_lettered_at"));
        Assert.Null(await ColumnAsync(id, "last_error_code"));
    }

    [Fact]
    public async Task Stale_owner_that_finishes_while_a_new_owner_is_still_working_does_not_release_the_new_lease()
    {
        var id = await EnqueueAsync();
        var first = Assert.Single(await WithOutboxAsync(o => o.ClaimAsync(10, CancellationToken.None, "owner-a")));
        Clock.Advance(TimeSpan.FromSeconds(30));
        var second = Assert.Single(await WithOutboxAsync(o => o.ClaimAsync(10, CancellationToken.None, "owner-b")));

        Assert.False(await WithOutboxAsync(o => o.RescheduleAsync(first, "late", CancellationToken.None)));
        Assert.Equal("owner-b", await ColumnAsync(id, "lease_owner"));
        Assert.Empty(await WithOutboxAsync(o => o.ClaimAsync(10, CancellationToken.None, "owner-c"))); // B'nin lease'i yerinde
        Assert.True(await WithOutboxAsync(o => o.CompleteAsync(second, CancellationToken.None)));
    }

    [Fact]
    public async Task Transient_failure_is_retried_with_backoff_and_then_succeeds()
    {
        var id = await EnqueueAsync();
        _probe.FailuresRemaining = 1;

        Assert.Equal(1, await RunOutboxAsync());
        Assert.Equal(0, _probe.Handled);
        Assert.Equal("InvalidOperationException", await ColumnAsync(id, "last_error_code"));
        Assert.Equal(0, await RunOutboxAsync()); // geri çekilme süresi dolmadan yeniden denenmez

        Clock.Advance(TimeSpan.FromSeconds(3)); // 2 sn taban + en çok %20 jitter
        Assert.Equal(1, await RunOutboxAsync());
        Assert.Equal(1, _probe.Handled);
        Assert.Equal("2", await ColumnAsync(id, "attempt_count"));
        Assert.NotNull(await ColumnAsync(id, "processed_at"));
    }

    [Fact]
    public async Task Poison_message_is_dead_lettered_after_the_budget_and_does_not_block_other_messages()
    {
        var poison = await EnqueueAsync("test.no_such_handler");
        var healthy = await EnqueueAsync();

        for (var i = 0; i < 3; i++)
        {
            await RunOutboxAsync();
            Clock.Advance(TimeSpan.FromSeconds(30));
        }

        Assert.NotNull(await ColumnAsync(poison, "dead_lettered_at"));
        Assert.Null(await ColumnAsync(poison, "processed_at"));
        Assert.Equal("3", await ColumnAsync(poison, "attempt_count"));
        Assert.Equal("OutboxPayloadException", await ColumnAsync(poison, "last_error_code"));
        Assert.NotNull(await ColumnAsync(healthy, "processed_at"));
        Assert.Equal(1, _probe.Handled);
        Assert.Equal(0, await RunOutboxAsync());
    }

    [Fact]
    public async Task Handler_that_keeps_failing_is_dead_lettered_and_its_hook_runs_once()
    {
        var id = await EnqueueAsync();
        _probe.FailuresRemaining = 100;

        for (var i = 0; i < 5; i++)
        {
            await RunOutboxAsync();
            Clock.Advance(TimeSpan.FromSeconds(30));
        }

        Assert.NotNull(await ColumnAsync(id, "dead_lettered_at"));
        Assert.Equal("3", await ColumnAsync(id, "attempt_count"));
        Assert.Equal(1, _probe.DeadLettered);
        Assert.Equal(0, _probe.Handled);
    }

    [Fact]
    public async Task Message_claimed_by_a_worker_that_crashed_is_recovered_after_the_lease_expires()
    {
        var id = await EnqueueAsync();
        Assert.Single(await WithOutboxAsync(o => o.ClaimAsync(10, CancellationToken.None, "crashed-owner"))); // kiraladı ve çöktü

        Assert.Equal(0, await RunOutboxAsync());
        Clock.Advance(TimeSpan.FromSeconds(29));
        Assert.Equal(0, await RunOutboxAsync());
        Clock.Advance(TimeSpan.FromSeconds(1)); // lease_until == now
        Assert.Equal(1, await RunOutboxAsync());

        Assert.Equal(1, _probe.Handled);
        Assert.NotNull(await ColumnAsync(id, "processed_at"));
    }

    [Fact]
    public async Task Outbox_message_is_written_only_if_the_business_transaction_commits()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => Factory.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            return await sp.GetRequiredService<Loca.Api.Infrastructure.Tx.ITransactionRunner>().RunAsync<int>(async ct =>
            {
                sp.GetRequiredService<IOutbox>().Enqueue(await sp.GetRequiredService<IClock>().NowAsync(ct), "test.probe", new { n = 1 });
                await db.SaveChangesAsync(ct);
                throw new InvalidOperationException("business failure after the outbox row was written");
            }, CancellationToken.None);
        }));

        Assert.Equal(0, await CountAsync("SELECT count(*) FROM ops.outbox_messages"));
        Assert.Equal(0, await RunOutboxAsync());
    }

    [Fact]
    public async Task Runtime_role_cannot_tamper_with_scan_log_but_can_use_the_queue_tables()
    {
        var error = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => Database.ExecuteAsAppAsync("DELETE FROM tickets.scan_log"));
        Assert.Equal("42501", error.SqlState);
        Assert.Equal(0, await Database.ExecuteAsAppAsync("DELETE FROM ops.outbox_messages WHERE processed_at < now() - interval '7 days'"));
        var simulator = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => Database.ExecuteAsAppAsync("DROP TABLE pspsim.charges"));
        Assert.Equal("42501", simulator.SqlState);
    }
}
