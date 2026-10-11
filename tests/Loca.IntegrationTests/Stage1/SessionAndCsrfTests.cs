using System.Net;
using Loca.IntegrationTests.Infrastructure;
using Npgsql;

namespace Loca.IntegrationTests.Stage1;

public sealed class SessionAndCsrfTests(PostgresFixture postgres) : IntegrationTest(postgres)
{
    [Fact]
    public async Task Login_sets_host_prefixed_httponly_secure_cookie_and_me_returns_the_user()
    {
        var client = Factory.NewClient();
        await client.RegisterAsync("ayse@loca.test");
        var login = (await client.LoginAsync("ayse@loca.test")).ShouldBe(HttpStatusCode.OK);

        var sessionCookie = Assert.Single(login.SetCookies, c => c.StartsWith("__Host-loca-session=", StringComparison.Ordinal));
        Assert.Contains("httponly", sessionCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", sessionCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", sessionCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", sessionCookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("domain=", sessionCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("no-store", login.Header("Cache-Control"));

        var me = (await client.GetAsync("/api/auth/me")).ShouldBe(HttpStatusCode.OK);
        Assert.Equal("ayse@loca.test", me.Json["email"]!.GetValue<string>());
        Assert.False(me.Json["isPlatformAdmin"]!.GetValue<bool>());
        Assert.Equal("no-store", me.Header("Cache-Control"));
    }

    [Fact]
    public async Task Unauthenticated_request_gets_json_problem_not_a_login_redirect()
    {
        var client = Factory.NewClient();
        var response = await client.GetAsync("/api/auth/me");

        response.ShouldFail(HttpStatusCode.Unauthorized, "unauthenticated");
        Assert.Null(response.Header("Location"));
    }

    [Fact]
    public async Task Old_cookie_is_rejected_after_logout_even_when_replayed_verbatim()
    {
        var (client, _) = await SignedInAsync("mehmet@loca.test");
        var stolen = client.Clone(); // Logout öncesi çerez ve CSRF token'ının birebir kopyası.

        (await client.PostAsync("/api/auth/logout")).ShouldBe(HttpStatusCode.NoContent);

        (await stolen.GetAsync("/api/auth/me")).ShouldFail(HttpStatusCode.Unauthorized, "unauthenticated");
        (await stolen.PostAsync("/api/organizations", new { name = "Eski Çerez Organizasyonu" }))
            .ShouldFail(HttpStatusCode.Unauthorized, "unauthenticated");

        var revoked = await Database.ScalarAsAdminAsync<long>("SELECT count(*) FROM identity.user_sessions WHERE revoked_at IS NOT NULL AND revoke_reason = 'Logout'");
        Assert.Equal(1, revoked);
        Assert.Equal(0, await Database.ScalarAsAdminAsync<long>("SELECT count(*) FROM identity.organizations"));
    }

    [Fact]
    public async Task Logout_all_revokes_every_session_of_the_user()
    {
        var (first, _) = await SignedInAsync("zeynep@loca.test");
        var second = Factory.NewClient();
        (await second.LoginAsync("zeynep@loca.test")).ShouldBe(HttpStatusCode.OK);

        (await first.PostAsync("/api/auth/logout-all")).ShouldBe(HttpStatusCode.NoContent);

        (await second.GetAsync("/api/auth/me")).ShouldFail(HttpStatusCode.Unauthorized, "unauthenticated");
    }

    [Fact]
    public async Task Session_expires_at_absolute_lifetime_and_on_idle_timeout_using_the_server_clock()
    {
        var (idle, _) = await SignedInAsync("idle@loca.test");
        Clock.Advance(TimeSpan.FromMinutes(119));
        (await idle.GetAsync("/api/auth/me")).ShouldBe(HttpStatusCode.OK); // boşta kalma sınırının hemen altı
        Clock.Advance(TimeSpan.FromMinutes(120));
        (await idle.GetAsync("/api/auth/me")).ShouldFail(HttpStatusCode.Unauthorized, "unauthenticated");

        var active = Factory.NewClient();
        (await active.LoginAsync("idle@loca.test")).ShouldBe(HttpStatusCode.OK);
        for (var i = 0; i < 7; i++)
        {
            Clock.Advance(TimeSpan.FromMinutes(100)); // sürekli etkin; boşta kalma tetiklenmez (toplam 700 dk < 720 dk)
            (await active.GetAsync("/api/auth/me")).ShouldBe(HttpStatusCode.OK);
        }
        Clock.Advance(TimeSpan.FromMinutes(20)); // tam 720. dakika: now == expiresAt → süresi dolmuş
        (await active.GetAsync("/api/auth/me")).ShouldFail(HttpStatusCode.Unauthorized, "unauthenticated");
    }

    [Fact]
    public async Task Disabled_user_loses_access_immediately_and_cannot_log_in()
    {
        var (client, userId) = await SignedInAsync("kapali@loca.test");
        await Database.ExecuteAsAdminAsync("UPDATE identity.users SET disabled_at = now() WHERE id = @id", new NpgsqlParameter("id", userId));

        (await client.GetAsync("/api/auth/me")).ShouldFail(HttpStatusCode.Unauthorized, "unauthenticated");
        var fresh = Factory.NewClient();
        (await fresh.LoginAsync("kapali@loca.test")).ShouldFail(HttpStatusCode.Unauthorized, "invalid_credentials");
    }

    [Fact]
    public async Task Unsafe_request_without_csrf_token_is_rejected()
    {
        var anonymous = Factory.NewClient();
        (await anonymous.PostAsync("/api/auth/login", new { email = "x@loca.test", password = "irrelevant-123" }, csrf: false))
            .ShouldFail(HttpStatusCode.Forbidden, "csrf_invalid");

        var (client, _) = await SignedInAsync("csrf@loca.test");
        (await client.PostAsync("/api/organizations", new { name = "CSRF Olmadan" }, csrf: false))
            .ShouldFail(HttpStatusCode.Forbidden, "csrf_invalid");
        Assert.Equal(0, await Database.ScalarAsAdminAsync<long>("SELECT count(*) FROM identity.organizations"));
    }

    [Fact]
    public async Task Anonymous_csrf_token_stops_working_after_login_and_the_login_token_works()
    {
        var client = Factory.NewClient();
        await client.RegisterAsync("token@loca.test");
        var anonymousToken = client.CsrfToken;
        (await client.LoginAsync("token@loca.test")).ShouldBe(HttpStatusCode.OK);
        var authenticatedToken = client.CsrfToken;
        Assert.NotEqual(anonymousToken, authenticatedToken);

        client.CsrfToken = anonymousToken;
        (await client.PostAsync("/api/organizations", new { name = "Anonim Token" })).ShouldFail(HttpStatusCode.Forbidden, "csrf_invalid");

        client.CsrfToken = authenticatedToken;
        (await client.PostAsync("/api/organizations", new { name = "Doğru Token" })).ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Csrf_endpoint_is_not_cacheable_and_other_gets_do_not_issue_antiforgery_cookies()
    {
        var client = Factory.NewClient();
        var csrf = (await client.GetAsync("/api/auth/csrf")).ShouldBe(HttpStatusCode.OK);
        Assert.Contains("no-store", csrf.Header("Cache-Control"));
        var csrfCookie = Assert.Single(csrf.SetCookies, c => c.StartsWith("__Host-loca-csrf=", StringComparison.Ordinal));
        Assert.Contains("httponly", csrfCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", csrfCookie, StringComparison.OrdinalIgnoreCase);

        var other = Factory.NewClient();
        foreach (var url in new[] { "/health/live", "/health/ready", "/openapi/v1.json" })
        {
            var response = (await other.GetAsync(url)).ShouldBe(HttpStatusCode.OK);
            Assert.DoesNotContain(response.SetCookies, c => c.Contains("csrf", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public async Task Account_locks_after_repeated_failures_and_the_response_does_not_reveal_it()
    {
        var client = Factory.NewClient();
        await client.RegisterAsync("kilit@loca.test");
        for (var i = 0; i < 5; i++)
            (await client.LoginAsync("kilit@loca.test", "yanlis-parola-00")).ShouldFail(HttpStatusCode.Unauthorized, "invalid_credentials");

        // Kilitliyken doğru parola da aynı genel hatayı alır.
        (await client.LoginAsync("kilit@loca.test")).ShouldFail(HttpStatusCode.Unauthorized, "invalid_credentials");
        (await client.LoginAsync("yok@loca.test")).ShouldFail(HttpStatusCode.Unauthorized, "invalid_credentials");
    }

    [Fact]
    public async Task Registration_rejects_duplicates_and_weak_passwords()
    {
        var client = Factory.NewClient();
        await client.RegisterAsync("tek@loca.test");
        (await client.PostAsync("/api/auth/register", new { email = "TEK@loca.test", password = TestUsers.Password, displayName = "İkinci" }))
            .ShouldFail(HttpStatusCode.Conflict, "email_already_registered");
        (await client.PostAsync("/api/auth/register", new { email = "zayif@loca.test", password = "kisa1", displayName = "Zayıf" }))
            .ShouldFail(HttpStatusCode.BadRequest, "weak_password");
    }
}
