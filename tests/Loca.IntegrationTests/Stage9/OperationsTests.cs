using System.Net;
using System.Text.RegularExpressions;
using Loca.Api.Infrastructure.Metrics;
using Loca.Api.Modules.Identity;
using Loca.Api.Modules.Operations;
using Loca.Api.Modules.Orders;
using Loca.IntegrationTests.Infrastructure;
using Loca.IntegrationTests.Stage6;
using Loca.IntegrationTests.Stage7;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Loca.IntegrationTests.Stage9;

public sealed class SalesSummaryTests(PostgresFixture postgres) : TicketTest(postgres)
{
    [Fact]
    public async Task Summary_metrics_follow_their_documented_definitions()
    {
        await ArrangeAsync(Clock.Now.AddDays(10));
        var c = await CustomersAsync(6, "ozet");

        await PaidOrderAsync(c[0], 0, 1);                                   // A: ödendi, 2 koltuk (900,00)
        var cancelledRefunded = await PaidOrderAsync(c[1], 2);              // B: ödendi → iptal → iade başarılı (450,00)
        (await c[1].CancelOrderAsync(cancelledRefunded)).ShouldBe(HttpStatusCode.OK);
        var (_, refundFails) = await StartCheckoutAsync("tok_refund_fail", c[2], 3); // C: ödendi → iptal → iade başarısız (450,00)
        await DrainAsync();
        (await c[2].CancelOrderAsync(refundFails)).ShouldBe(HttpStatusCode.OK);
        await DrainAsync();
        var (_, late) = await StartCheckoutAsync("tok_late_success", c[3], 4);        // D: geç başarı (450,00), iade bekliyor
        await RunOutboxAsync();
        (await c[4].HoldSeatsAsync(Seated.SessionId, [Seated.SeatIds[5]])).ShouldBe(HttpStatusCode.Created); // F: süresi dolacak, toplanmayacak hold

        Clock.Advance(TimeSpan.FromSeconds(600)); // D ve F'nin hold süresi doldu (worker çalışmadı)
        Assert.Equal(ApplyOutcome.Applied, await ApplyAsync(late, ProviderChargeStatus.Succeeded, 2));
        (await c[5].HoldSeatsAsync(Seated.SessionId, [Seated.SeatIds[6]])).ShouldBe(HttpStatusCode.Created); // E: aktif hold

        var summary = (await Organizer.GetAsync($"/api/organizer/sessions/{Seated.SessionId}/summary")).ShouldBe(HttpStatusCode.OK).Json;

        Assert.Equal(10, summary["capacity"]!.GetValue<int>());
        Assert.Equal(2, summary["sold"]!.GetValue<int>());               // yalnız A; iptal edilenlerin koltukları yeniden açıldı
        Assert.Equal(1, summary["held"]!.GetValue<int>());               // yalnız E; süresi dolmuş F sayılmaz
        Assert.Equal(7, summary["available"]!.GetValue<int>());
        Assert.Equal(2, summary["ticketsSold"]!.GetValue<int>());
        Assert.Equal(0, summary["ticketsCheckedIn"]!.GetValue<int>());
        Assert.Equal(225000, summary["grossCapturedMinor"]!.GetValue<long>()); // A + B + C + D: başarılı tüm tahsilatlar (geç başarı dahil)
        Assert.Equal(45000, summary["refundedMinor"]!.GetValue<long>());       // yalnız gerçekten başarılı iade (B)
        Assert.Equal(90000, summary["refundPendingMinor"]!.GetValue<long>());  // C (başarısız) + D (bekliyor): henüz geri ödenmedi
        Assert.Equal(180000, summary["netMinor"]!.GetValue<long>());           // brüt − başarılı iade
        Assert.Equal(1, summary["ordersPaid"]!.GetValue<int>());
        Assert.Equal(2, summary["ordersCancelled"]!.GetValue<int>());
        Assert.Equal("TRY", summary["currency"]!.GetValue<string>());
        foreach (var personal in new[] { "email", "displayName", "ozet-0", "token" })
            Assert.DoesNotContain(personal, summary.ToJsonString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task General_admission_summary_counts_only_unexpired_holds_as_held()
    {
        var (organizer, _, organizationId) = await OrganizerAsync("org-ga@loca.test", "Martı Etkinlik");
        var ga = await CatalogSetup.GaSessionAsync(organizer, organizationId, Clock.Now.AddDays(5), [("Genel Giriş", 25000, 20), ("Sahne Önü", 40000, 10)]);
        var c = await CustomersAsync(3, "ga");
        (await c[0].HoldGaAsync(ga.SessionId, null, (ga.CategoryIds[0], 4))).ShouldBe(HttpStatusCode.Created);
        Clock.Advance(TimeSpan.FromSeconds(600)); // ilk hold doldu; sayaçta hâlâ duruyor
        (await c[1].HoldGaAsync(ga.SessionId, null, (ga.CategoryIds[1], 2))).ShouldBe(HttpStatusCode.Created);
        var orderId = (await c[2].CheckoutAsync((await c[2].HoldGaAsync(ga.SessionId, null, (ga.CategoryIds[0], 3))).HoldId())).OrderId();
        await DrainAsync();

        var summary = (await organizer.GetAsync($"/api/organizer/sessions/{ga.SessionId}/summary")).ShouldBe(HttpStatusCode.OK).Json;
        Assert.Equal(30, summary["capacity"]!.GetValue<int>());
        Assert.Equal(3, summary["sold"]!.GetValue<int>());
        Assert.Equal(2, summary["held"]!.GetValue<int>());
        Assert.Equal(25, summary["available"]!.GetValue<int>());
        Assert.Equal(75000, summary["grossCapturedMinor"]!.GetValue<long>());
        Assert.Equal(75000, summary["netMinor"]!.GetValue<long>());
        Assert.NotEqual(Guid.Empty, orderId);
    }

    [Fact]
    public async Task Summary_and_operations_are_restricted_to_the_owning_organizer_and_platform_admin()
    {
        await ArrangeGateAsync();
        await PaidOrderAsync();
        var (otherOrganizer, _, otherOrganization) = await OrganizerAsync("org-b@loca.test", "Martı Etkinlik");
        var admin = await SignedInPlatformAdminAsync();

        (await otherOrganizer.GetAsync($"/api/organizer/sessions/{Seated.SessionId}/summary")).ShouldFail(HttpStatusCode.NotFound, "not_found");
        (await otherOrganizer.GetAsync($"/api/organizer/organizations/{OrganizationId}/operations")).ShouldFail(HttpStatusCode.NotFound, "not_found");
        (await Customer.GetAsync($"/api/organizer/sessions/{Seated.SessionId}/summary")).ShouldFail(HttpStatusCode.NotFound, "not_found");
        (await Gate.GetAsync($"/api/organizer/sessions/{Seated.SessionId}/summary")).ShouldFail(HttpStatusCode.Forbidden, "forbidden"); // kapı görevlisi finansı göremez
        (await Gate.GetAsync($"/api/organizer/organizations/{OrganizationId}/operations")).ShouldFail(HttpStatusCode.Forbidden, "forbidden");
        foreach (var url in new[] { "/api/admin/operations", "/api/admin/audit", "/api/admin/outbox/dead-letters", "/api/admin/metrics", "/api/admin/events", "/api/admin/demo-psp/charges" })
        {
            (await Organizer.GetAsync(url)).ShouldFail(HttpStatusCode.Forbidden, "forbidden");
            (await Customer.GetAsync(url)).ShouldFail(HttpStatusCode.Forbidden, "forbidden");
            (await Factory.NewClient().GetAsync(url)).ShouldFail(HttpStatusCode.Unauthorized, "unauthenticated");
            (await admin.GetAsync(url)).ShouldBe(HttpStatusCode.OK);
        }
        // Platform yöneticisi organizatörün seansını YÖNETEMEZ; yalnız operasyon düzeyinde görür.
        (await admin.GetAsync($"/api/organizer/sessions/{Seated.SessionId}/summary")).ShouldFail(HttpStatusCode.NotFound, "not_found");
        Assert.Empty((await otherOrganizer.GetAsync($"/api/organizer/organizations/{otherOrganization}/operations")).ShouldBe(HttpStatusCode.OK).Json["items"]!.AsArray());
    }
}

public sealed class OperationsQueueTests(PostgresFixture postgres) : TicketTest(postgres)
{
    protected override IDictionary<string, string?> Settings => new Dictionary<string, string?> { ["Outbox:MaxAttempts"] = "2" };

    [Fact]
    public async Task Queue_lists_open_recovery_items_and_counts_only_real_successes()
    {
        await ArrangeAsync(Clock.Now.AddDays(10));
        var c = await CustomersAsync(3, "kuyruk");
        var (_, unknown) = await StartCheckoutAsync("tok_timeout_success", c[0], 0);
        await RunOutboxAsync(); // belirsiz ödeme
        var (_, refundFails) = await StartCheckoutAsync("tok_refund_fail", c[1], 1);
        await DrainAsync();
        (await c[1].CancelOrderAsync(refundFails)).ShouldBe(HttpStatusCode.OK);
        await DrainAsync(); // başarısız iade
        await PaidOrderAsync(c[2], 2); // sorunsuz sipariş: kuyrukta görünmez

        var url = $"/api/organizer/organizations/{OrganizationId}/operations";
        var queue = (await Organizer.GetAsync(url)).ShouldBe(HttpStatusCode.OK).Json;
        var items = queue["items"]!.AsArray();
        Assert.Equal(2, items.Count);
        var payment = items.Single(i => i!["kind"]!.GetValue<string>() == "payment")!;
        var refund = items.Single(i => i!["kind"]!.GetValue<string>() == "refund")!;
        Assert.Equal("Unknown", payment["status"]!.GetValue<string>());
        Assert.Equal(["reconcile"], payment["actions"]!.AsArray().Select(a => a!.GetValue<string>()));
        Assert.Equal("Failed", refund["status"]!.GetValue<string>());
        Assert.Equal("refund_not_permitted", refund["detail"]!.GetValue<string>());
        Assert.Equal(["retry"], refund["actions"]!.AsArray().Select(a => a!.GetValue<string>()));
        Assert.Equal(1, queue["counts"]!["unknownPayments"]!.GetValue<int>());
        Assert.Equal(1, queue["counts"]!["failedRefunds"]!.GetValue<int>());
        Assert.Equal(0, queue["counts"]!["runsSucceeded"]!.GetValue<int>());
        foreach (var personal in new[] { "kuyruk-0", "@loca.test", "displayName", "token" })
            Assert.DoesNotContain(personal, queue.ToJsonString(), StringComparison.OrdinalIgnoreCase);

        // Başarısız deneme (sağlayıcıya ulaşılamadı), başarısız iade denemesi ve başarılı reconcile ayrı sayılır.
        ProviderFaults.RefuseRequests(1);
        (await Organizer.PostAsync($"/api/organizer/payments/{payment["id"]!.GetValue<Guid>()}/reconcile")).ShouldBe(HttpStatusCode.OK);
        (await Organizer.PostAsync($"/api/organizer/refunds/{refund["id"]!.GetValue<Guid>()}/retry")).ShouldBe(HttpStatusCode.OK);
        (await Organizer.PostAsync($"/api/organizer/payments/{payment["id"]!.GetValue<Guid>()}/reconcile")).ShouldBe(HttpStatusCode.OK);

        var after = (await Organizer.GetAsync(url)).ShouldBe(HttpStatusCode.OK).Json;
        Assert.Equal(1, after["counts"]!["runsSucceeded"]!.GetValue<int>());
        Assert.Equal(2, after["counts"]!["runsFailed"]!.GetValue<int>());
        Assert.Equal(0, after["counts"]!["unknownPayments"]!.GetValue<int>());
        Assert.Equal(1, after["counts"]!["failedRefunds"]!.GetValue<int>()); // iade hâlâ başarısız: "çözüldü" sayılmaz
        Assert.Single(after["items"]!.AsArray());
        Assert.Equal(3, after["recentRuns"]!.AsArray().Count);
        Assert.Equal("Paid", await OrderStatusAsync(unknown));

        var admin = await SignedInPlatformAdminAsync();
        Assert.Single((await admin.GetAsync("/api/admin/operations")).ShouldBe(HttpStatusCode.OK).Json["items"]!.AsArray());
        var metrics = (await admin.GetAsync("/api/admin/metrics")).ShouldBe(HttpStatusCode.OK).Json;
        Assert.Equal(1, metrics["loca_operator_runs_total{outcome=\"succeeded\",type=\"payment\"}"]!.GetValue<long>());
        Assert.Equal(1, metrics["loca_operator_runs_total{outcome=\"failed\",type=\"payment\"}"]!.GetValue<long>());
        Assert.Equal(1, metrics["loca_operator_runs_total{outcome=\"failed\",type=\"refund\"}"]!.GetValue<long>());
    }

    [Fact]
    public async Task Dead_lettered_message_is_visible_to_the_admin_and_replay_completes_the_original_intent()
    {
        await ArrangeAsync();
        var (_, orderId) = await StartCheckoutAsync("tok_success");
        await RunOutboxAsync(); // ödendi; bilet niyeti outbox'ta
        // Bilet üretimi iki kez "başarısız" olmuş gibi: mesaj dead-letter'a düşmüş.
        await Database.ExecuteAsAdminAsync("UPDATE ops.outbox_messages SET dead_lettered_at = now(), attempt_count = 2, last_error_code = 'TimeoutException' WHERE type = 'order.paid'");
        Assert.Equal(0, await RunOutboxAsync());
        Assert.Equal(0, await TicketsAsync(orderId));

        var admin = await SignedInPlatformAdminAsync();
        var deadLetters = (await admin.GetAsync("/api/admin/outbox/dead-letters")).ShouldBe(HttpStatusCode.OK).Json.AsArray();
        var message = Assert.Single(deadLetters)!;
        Assert.Equal("order.paid", message["type"]!.GetValue<string>());
        var messageId = message["id"]!.GetValue<Guid>();

        (await Organizer.PostAsync($"/api/admin/outbox/{messageId}/replay")).ShouldFail(HttpStatusCode.Forbidden, "forbidden");
        (await admin.PostAsync($"/api/admin/outbox/{messageId}/replay")).ShouldBe(HttpStatusCode.OK);
        (await admin.PostAsync($"/api/admin/outbox/{messageId}/replay")).ShouldFail(HttpStatusCode.NotFound, "not_found"); // artık dead-letter değil
        await DrainAsync();

        Assert.Equal(1, await TicketsAsync(orderId));
        Assert.Empty((await admin.GetAsync("/api/admin/outbox/dead-letters")).Json.AsArray());
        Assert.Contains((await admin.GetAsync("/api/admin/audit?limit=50")).Json.AsArray(), a => a!["action"]!.GetValue<string>() == "outbox.replayed");
    }

    [Fact]
    public async Task Demo_psp_panel_lets_the_platform_admin_resolve_a_pending_charge()
    {
        await ArrangeAsync();
        var (_, orderId) = await StartCheckoutAsync("tok_late_success");
        await RunOutboxAsync();
        var admin = await SignedInPlatformAdminAsync();

        var pending = (await admin.GetAsync("/api/admin/demo-psp/charges?status=pending")).ShouldBe(HttpStatusCode.OK).Json.AsArray();
        var reference = Assert.Single(pending)!["merchantReference"]!.GetValue<string>();
        Assert.Equal(PurchaseApi.MerchantReference(orderId), reference);

        (await Organizer.PostAsync($"/api/admin/demo-psp/charges/{reference}/resolve", new { outcome = "succeeded" })).ShouldFail(HttpStatusCode.Forbidden, "forbidden");
        (await admin.PostAsync($"/api/admin/demo-psp/charges/{reference}/resolve", new { outcome = "refunded" })).ShouldFail(HttpStatusCode.BadRequest, "validation_failed");
        (await admin.PostAsync("/api/admin/demo-psp/charges/pay_does_not_exist/resolve", new { outcome = "succeeded" })).ShouldFail(HttpStatusCode.NotFound, "not_found");
        (await admin.PostAsync($"/api/admin/demo-psp/charges/{reference}/resolve", new { outcome = "succeeded" })).ShouldBe(HttpStatusCode.OK);
        await DrainAsync(); // simülatör webhook'u teslim eder

        Assert.Equal("Paid", await OrderStatusAsync(orderId));
        Assert.Equal(1, await TicketsAsync(orderId));
    }
}

public sealed class DemoCrowdTests(PostgresFixture postgres) : TicketTest(postgres)
{
    private Task<Guid?> SimulateAsync() => Factory.WithScopeAsync(sp => sp.GetRequiredService<DemoCrowdProcessor>().SimulateOnePurchaseAsync(CancellationToken.None));

    private Task CreateBotsAsync(int count) => Factory.WithScopeAsync(async sp =>
    {
        var users = sp.GetRequiredService<UserManager<AppUser>>();
        for (var i = 1; i <= count; i++)
        {
            var email = $"{DemoCrowdProcessor.BotEmailPrefix}{i}@loca.demo";
            var result = await users.CreateAsync(new AppUser { Id = Guid.CreateVersion7(), UserName = email, Email = email, DisplayName = $"Demo İzleyici {i}", IsDemo = true, CreatedAt = Clock.Now });
            Assert.True(result.Succeeded);
        }
        return 0;
    });

    [Fact]
    public async Task Synthetic_purchases_happen_only_on_opted_in_sessions_use_the_normal_purchase_path_and_leave_room_for_real_users()
    {
        await ArrangeAsync(demoSimulator: false);
        await CreateBotsAsync(4);
        Assert.Null(await SimulateAsync()); // seans opt-in değil

        (await Organizer.PutAsync($"/api/organizer/sessions/{Seated.SessionId}/demo-simulator", new { enabled = true })).ShouldBe(HttpStatusCode.OK);
        var purchases = new List<Guid>();
        for (var i = 0; i < 12; i++)
        {
            if (await SimulateAsync() is { } orderId) purchases.Add(orderId);
            await DrainAsync();
        }

        Assert.NotEmpty(purchases);
        Assert.All(purchases, o => Assert.Equal("Paid", OrderStatusAsync(o).Result));
        // Normal yol kullanıldı: sipariş, ödeme, bilet ve denetim kaydı gerçek akışla aynı tablolarda.
        Assert.Equal(purchases.Count, await CountAsync("SELECT count(*) FROM orders.orders o JOIN identity.users u ON u.id = o.user_id WHERE u.is_demo"));
        Assert.Equal(await CountAsync("SELECT count(*) FROM inventory.session_seats WHERE status = 'Sold'"), await CountAsync("SELECT count(*) FROM tickets.tickets WHERE status = 'Valid'"));
        Assert.Equal(purchases.Count, await CountAsync("SELECT count(*) FROM ops.audit_log WHERE action = 'hold.created' AND actor_kind = 'Demo'"));
        // Müsaitlik yarının altına indiğinde sentetik alım durur.
        Assert.True(await CountAsync("SELECT count(*) FROM inventory.session_seats WHERE status = 'Available'") >= 3);
        Assert.Null(await SimulateAsync());

        // Demo izleyici hesapları parolasızdır: giriş yapılamaz.
        var bot = Factory.NewClient();
        (await bot.LoginAsync($"{DemoCrowdProcessor.BotEmailPrefix}1@loca.demo", "herhangi-bir-parola-1")).ShouldFail(HttpStatusCode.Unauthorized, "invalid_credentials");
    }
}

public sealed class ObservabilityTests(PostgresFixture postgres) : TicketTest(postgres)
{
    protected override IDictionary<string, string?> Settings => new Dictionary<string, string?>
    {
        // Sızıntı testinde her şey loglansın: uygulama, EF komutları ve ASP.NET Core dahil.
        ["Logging:LogLevel:Default"] = "Debug",
        ["Logging:LogLevel:Microsoft.EntityFrameworkCore"] = "Information",
        ["Logging:LogLevel:Microsoft.AspNetCore"] = "Information",
        ["Logging:LogLevel:Loca.Api.Infrastructure.Outbox"] = "Debug",
    };

    [Fact]
    public async Task Logs_of_a_complete_purchase_and_check_in_contain_no_secret_or_personal_data()
    {
        await ArrangeGateAsync();
        var orderId = await PaidOrderAsync();
        var ticket = (await TicketsWithTokensAsync(Customer)).Single();
        (await ScanAsync(Gate, ticket.Token)).ShouldBe(HttpStatusCode.OK);
        (await ScanAsync(Gate, "LT1.yanlisAmaBicimliOlmayanToken")).ShouldBe(HttpStatusCode.NotFound);
        var failedLogin = Factory.NewClient();
        (await failedLogin.LoginAsync("musteri@loca.test", "Yanlis-Parola-Gizli-99")).ShouldBe(HttpStatusCode.Unauthorized);
        var body = WebhookSender.Event("evt_log", "charge.succeeded", orderId, SeatPrice, 5);
        await WebhookSender.SendAsync(Factory.NewClient(), body, Clock.Now, secret: "attacker-secret-value-123456");

        var logs = Factory.Logs.All;
        Assert.Contains("HTTP POST /api/gate/sessions/{sessionId:guid}/check-ins responded 200", logs); // erişim logu rota şablonu yazar
        Assert.Contains("correlationId=", logs);

        var forbidden = new Dictionary<string, string>
        {
            ["bilet token'ı"] = ticket.Token,
            ["bilet token'ı (gövde)"] = ticket.Token[4..],
            ["hatalı token"] = "yanlisAmaBicimliOlmayanToken",
            ["parola"] = TestUsers.Password,
            ["hatalı parola"] = "Yanlis-Parola-Gizli-99",
            ["müşteri e-postası"] = "musteri@loca.test",
            ["kapı e-postası"] = "kapi@loca.test",
            ["PSP API anahtarı"] = LocaFactory.ApiKey,
            ["webhook secret"] = LocaFactory.WebhookSecret,
            ["oturum çerezi"] = Customer.Cookies["__Host-loca-session"],
            ["CSRF çerezi"] = Customer.Cookies["__Host-loca-csrf"],
            ["CSRF token'ı"] = Customer.CsrfToken!,
            ["kapı oturum çerezi"] = Gate.Cookies["__Host-loca-session"],
        };
        foreach (var (name, value) in forbidden)
            Assert.False(logs.Contains(value, StringComparison.Ordinal), $"Loglarda {name} bulundu.");
        Assert.DoesNotMatch(new Regex(@"[A-Za-z0-9._%+-]+@loca\.test"), logs);
        Assert.DoesNotContain("LT1.", logs);
        Assert.DoesNotContain("Demo musteri", logs); // görünen ad
    }

    [Fact]
    public async Task Metrics_use_only_allowed_low_cardinality_labels()
    {
        await ArrangeGateAsync();
        var (_, declined) = await StartCheckoutAsync("tok_decline", null, 0);
        await DrainAsync();
        await PaidOrderAsync(null, 0);
        var ticket = (await TicketsWithTokensAsync(Customer)).Single();
        (await ScanAsync(Gate, ticket.Token)).ShouldBe(HttpStatusCode.OK);
        (await ScanAsync(Gate, ticket.Token)).ShouldBe(HttpStatusCode.Conflict);
        (await Customer.HoldSeatsAsync(Seated.SessionId, [Seated.SeatIds[0]])).ShouldBe(HttpStatusCode.Conflict);

        var admin = await SignedInPlatformAdminAsync();
        var metrics = (await admin.GetAsync("/api/admin/metrics")).ShouldBe(HttpStatusCode.OK);
        var series = metrics.Json.AsObject().Select(kv => kv.Key).ToList();

        Assert.Contains("loca_holds_total{outcome=\"created\"}", series);
        Assert.Contains("loca_holds_total{outcome=\"seats_unavailable\"}", series);
        Assert.Contains("loca_payments_total{outcome=\"paid\"}", series);
        Assert.Contains("loca_payments_total{outcome=\"declined\"}", series);
        Assert.Contains("loca_checkins_total{result=\"accepted\"}", series);
        Assert.Contains("loca_checkins_total{result=\"already_checked_in\"}", series);
        Assert.Contains("loca_outbox_total{outcome=\"processed\"}", series);
        Assert.Contains("loca_transaction_retries_total", series);

        var labelKey = new Regex(@"([a-z_]+)=""");
        var uuid = new Regex(@"[0-9a-f]{8}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{12}", RegexOptions.IgnoreCase);
        foreach (var name in series)
        {
            Assert.All(labelKey.Matches(name).Select(m => m.Groups[1].Value), key => Assert.Contains(key, AppMetrics.AllowedLabelKeys));
            Assert.DoesNotMatch(uuid, name);
            Assert.DoesNotContain("@", name);
            Assert.DoesNotContain("LC-", name);
        }
        Assert.True(series.Count < 40, "Metric series count should stay small.");
        Assert.NotEqual(Guid.Empty, declined);
    }

    [Fact]
    public void Metric_registry_rejects_identifiers_and_unknown_label_keys()
    {
        using var metrics = new AppMetrics();
        metrics.Increment("ok_total", ("outcome", "created"));
        Assert.Throws<ArgumentException>(() => metrics.Increment("bad_total", ("order_id", "x")));
        Assert.Throws<ArgumentException>(() => metrics.Increment("bad_total", ("outcome", Guid.NewGuid().ToString())));
        Assert.Throws<ArgumentException>(() => metrics.Increment("bad_total", ("outcome", "musteri@loca.test")));
        Assert.Throws<ArgumentException>(() => metrics.Increment("bad_total", ("outcome", new string('x', 60))));
        Assert.Equal(["ok_total{outcome=\"created\"}"], metrics.Snapshot().Keys);
    }

    [Fact]
    public async Task OpenApi_document_describes_the_api_without_secrets_or_simulator_internals()
    {
        var document = (await Factory.NewClient().GetAsync("/openapi/v1.json")).ShouldBe(HttpStatusCode.OK);

        foreach (var path in new[] { "/api/auth/login", "/api/catalog/sessions", "/api/sessions/{sessionId}/holds", "/api/holds/{holdId}/checkout",
                     "/api/orders/{orderId}/cancel", "/api/gate/sessions/{sessionId}/check-ins", "/api/webhooks/psp", "/api/organizer/sessions/{sessionId}/summary" })
            Assert.Contains($"\"{path}\"", document.Text);
        Assert.DoesNotContain("/sim-psp", document.Text);
        foreach (var secret in new[] { LocaFactory.ApiKey, LocaFactory.WebhookSecret, TestUsers.Password, "Password=", "ConnectionString" })
            Assert.DoesNotContain(secret, document.Text, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class RateLimitTests(PostgresFixture postgres) : IntegrationTest(postgres)
{
    protected override IDictionary<string, string?> Settings => new Dictionary<string, string?>
    {
        ["RateLimits:LoginPerMinute"] = "3", ["RateLimits:HoldPerMinute"] = "2",
    };

    [Fact]
    public async Task Login_attempts_are_limited_per_address_and_spoofed_forwarded_headers_do_not_reset_the_limit()
    {
        var client = Factory.NewClient();
        await client.FetchCsrfAsync();
        async Task<ApiResponse> Attempt(string forwardedFor) => await client.SendAsync(HttpMethod.Post, "/api/auth/login",
            new { email = "yok@loca.test", password = "Deneme-Parola-2026" }, headers: new Dictionary<string, string> { ["X-Forwarded-For"] = forwardedFor });

        for (var i = 0; i < 3; i++) (await Attempt($"203.0.113.{i}")).ShouldFail(HttpStatusCode.Unauthorized, "invalid_credentials");
        // Güvenilir proxy yapılandırılmadığı için X-Forwarded-For dikkate alınmaz: farklı "adres" yeni kova açmaz.
        var limited = (await Attempt("198.51.100.77")).ShouldFail(HttpStatusCode.TooManyRequests, "rate_limited");
        Assert.NotNull(limited.Header("Retry-After"));
        Assert.True(limited.Json["retryAfterSeconds"]!.GetValue<int>() > 0);
    }

    [Fact]
    public async Task Authenticated_limits_are_per_user_so_one_customer_cannot_exhaust_another_customers_budget()
    {
        var (organizer, _, organizationId) = await OrganizerAsync("org-a@loca.test", "Kumru Sahne");
        var setup = await CatalogSetup.SeatedSessionAsync(organizer, organizationId, Clock.Now.AddDays(10));
        var customers = await CustomersAsync(2);

        (await customers[0].HoldSeatsAsync(setup.SessionId, [setup.SeatIds[0]])).ShouldBe(HttpStatusCode.Created);
        (await customers[0].HoldSeatsAsync(setup.SessionId, [setup.SeatIds[1]])).ShouldFail(HttpStatusCode.Conflict, "active_hold_exists");
        (await customers[0].HoldSeatsAsync(setup.SessionId, [setup.SeatIds[2]])).ShouldFail(HttpStatusCode.TooManyRequests, "rate_limited");

        // İkinci müşterinin kendi kovası vardır.
        (await customers[1].HoldSeatsAsync(setup.SessionId, [setup.SeatIds[3]])).ShouldBe(HttpStatusCode.Created);
    }
}
