using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Loca.Api.Infrastructure.Http;
using Loca.Api.Infrastructure.Idempotency;
using Loca.Api.Infrastructure.Outbox;
using Loca.Api.Modules.Inventory;
using Loca.Api.Modules.Orders;
using Loca.Api.PspSimulator;
using Loca.Api.Infrastructure.Persistence;
using Loca.Api.Infrastructure.Time;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Loca.IntegrationTests.Infrastructure;

/// <summary>Test host'unda veritabanı saatinin yerini alır; süre dolumu testleri uyumaz, saati ilerletir.</summary>
public sealed class ManualClock : IClock
{
    private long _ticks = new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero).UtcTicks;

    public DateTimeOffset Now => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
    public Task<DateTimeOffset> NowAsync(CancellationToken ct = default) => Task.FromResult(Now);
    public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    public void Set(DateTimeOffset value) => Interlocked.Exchange(ref _ticks, value.UtcTicks);
}

public sealed class LocaFactory(TestDatabase database, ManualClock? clock, IDictionary<string, string?>? settings = null,
    Action<IServiceCollection>? configureServices = null) : WebApplicationFactory<Program>
{
    public TestDatabase Database { get; } = database;
    public TestFailpoints Failpoints { get; } = new();
    public ProviderFaults ProviderFaults { get; } = new();
    public CapturingLoggerProvider Logs { get; } = new();

    public const string ApiKey = "test-psp-api-key-0123456789";
    public const string WebhookSecret = "test-webhook-secret-current-0123456789";
    public const string PreviousWebhookSecret = "test-webhook-secret-previous-0123456789";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Uygulama ilk bağlantıyı açmadan önce hedef doğrulanır.
        TestDatabaseGuard.EnsureOwnedAsync(Database.AppConnectionString, Database.Identity, requireMarker: true).GetAwaiter().GetResult();

        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration(config =>
        {
            var values = new Dictionary<string, string?>
            {
                ["Database:AppConnectionString"] = Database.AppConnectionString,
                ["Database:OwnerConnectionString"] = Database.OwnerConnectionString,
                ["Workers:Enabled"] = "false",
                ["RateLimits:LoginPerMinute"] = "100000",
                ["RateLimits:RegisterPerMinute"] = "100000",
                ["RateLimits:CatalogPerMinute"] = "100000",
                ["RateLimits:HoldPerMinute"] = "100000",
                ["RateLimits:CheckoutPerMinute"] = "100000",
                ["RateLimits:CheckInPerMinute"] = "100000",
                ["RateLimits:WebhookPerMinute"] = "100000",
                ["Logging:LogLevel:Default"] = "Warning",
                ["Logging:LogLevel:Microsoft.EntityFrameworkCore"] = "Critical",
                ["Logging:LogLevel:Loca.Api.Infrastructure.Outbox"] = "Critical",
                // Simüle PSP, gerçek HTTP hattı üzerinden (test sunucusu) çağrılır.
                ["Psp:BaseUrl"] = "https://localhost/sim-psp/",
                ["Psp:ApiKey"] = ApiKey,
                ["Psp:WebhookSecrets:0"] = WebhookSecret,
                ["Psp:Simulator:Enabled"] = "true",
                ["Psp:Simulator:WebhookTargetUrl"] = "https://localhost/api/webhooks/psp",
                ["Psp:Simulator:AsyncDelaySeconds"] = "0",
                ["Demo:SimulatorEnabled"] = "true",
            };
            if (settings is not null)
                foreach (var (key, value) in settings) values[key] = value;
            config.AddInMemoryCollection(values);
        });
        builder.ConfigureServices(services =>
        {
            if (clock is not null)
            {
                services.RemoveAll<IClock>();
                services.AddSingleton<IClock>(clock);
            }
            services.AddSingleton<Microsoft.Extensions.Logging.ILoggerProvider>(Logs);
            services.RemoveAll<IFailpoints>();
            services.AddSingleton<IFailpoints>(Failpoints);
            // Yalnız test host'u: parola özeti maliyeti düşürülür (çok kullanıcılı yarış testleri için).
            services.Configure<PasswordHasherOptions>(o => o.IterationCount = 1000);
            // Sağlayıcı ve webhook istemcileri test sunucusuna bağlanır; araya hata enjekte eden bir işleyici girer.
            services.AddHttpClient(PspOptions.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new FaultInjectingHandler(ProviderFaults, Server.CreateHandler()));
            services.AddHttpClient(PspSimulatorOptions.WebhookClientName).ConfigurePrimaryHttpMessageHandler(() => Server.CreateHandler());
            configureServices?.Invoke(services);
        });
    }

    public TestClient NewClient() => new(CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://localhost"),
        HandleCookies = false,
        AllowAutoRedirect = false,
    }));

    public async Task<T> WithDbAsync<T>(Func<AppDbContext, Task<T>> work)
    {
        await using var scope = Services.CreateAsyncScope();
        return await work(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    public async Task WithScopeAsync(Func<IServiceProvider, Task> work)
    {
        await using var scope = Services.CreateAsyncScope();
        await work(scope.ServiceProvider);
    }

    public async Task<T> WithScopeAsync<T>(Func<IServiceProvider, Task<T>> work)
    {
        await using var scope = Services.CreateAsyncScope();
        return await work(scope.ServiceProvider);
    }
}

public sealed record ApiResponse(HttpStatusCode Status, byte[] Body, HttpResponseHeaders Headers, HttpContentHeaders ContentHeaders)
{
    public string Text => Encoding.UTF8.GetString(Body);
    public JsonNode Json => JsonNode.Parse(Body) ?? throw new InvalidOperationException("Empty JSON body.");
    public string? Code => Body.Length == 0 ? null : Json["code"]?.GetValue<string>();
    public T As<T>() => JsonSerializer.Deserialize<T>(Body, JsonDefaults.Options)!;
    public string? Header(string name) => Headers.TryGetValues(name, out var v) || ContentHeaders.TryGetValues(name, out v) ? string.Join(",", v) : null;
    public IReadOnlyList<string> SetCookies => Headers.TryGetValues("Set-Cookie", out var v) ? [.. v] : [];

    public ApiResponse ShouldBe(HttpStatusCode expected)
    {
        Assert.True(Status == expected, $"Expected {(int)expected} but got {(int)Status}: {Text}");
        return this;
    }

    public ApiResponse ShouldFail(HttpStatusCode expected, string code)
    {
        ShouldBe(expected);
        Assert.Equal("application/problem+json", ContentHeaders.ContentType?.MediaType);
        Assert.Equal(code, Code);
        return this;
    }
}

/// <summary>
/// Çerezleri ve CSRF token'ını kendisi yöneten test istemcisi. Çerez kavanozu açıkça kopyalanabildiği
/// için "logout sonrası eski çerezi yeniden gönder" gibi senaryolar yazılabilir.
/// </summary>
public sealed class TestClient(HttpClient http)
{
    public Dictionary<string, string> Cookies { get; private set; } = new(StringComparer.Ordinal);
    public string? CsrfToken { get; set; }

    public TestClient Clone(HttpClient? other = null) => new(other ?? http) { Cookies = new(Cookies, StringComparer.Ordinal), CsrfToken = CsrfToken };

    public Task<ApiResponse> GetAsync(string url) => SendAsync(HttpMethod.Get, url);
    public Task<ApiResponse> PostAsync(string url, object? body = null, string? idempotencyKey = null, bool csrf = true) =>
        SendAsync(HttpMethod.Post, url, body, idempotencyKey, csrf);
    public Task<ApiResponse> PutAsync(string url, object? body = null, string? idempotencyKey = null) => SendAsync(HttpMethod.Put, url, body, idempotencyKey);
    public Task<ApiResponse> DeleteAsync(string url) => SendAsync(HttpMethod.Delete, url);

    public async Task<ApiResponse> SendAsync(HttpMethod method, string url, object? body = null, string? idempotencyKey = null,
        bool csrf = true, IDictionary<string, string>? headers = null, byte[]? rawBody = null)
    {
        using var request = new HttpRequestMessage(method, url);
        if (Cookies.Count > 0)
            request.Headers.Add("Cookie", string.Join("; ", Cookies.Select(c => $"{c.Key}={c.Value}")));
        if (csrf && CsrfToken is not null && method != HttpMethod.Get)
            request.Headers.Add("X-CSRF-TOKEN", CsrfToken);
        if (idempotencyKey is not null)
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        if (headers is not null)
            foreach (var (name, value) in headers) request.Headers.TryAddWithoutValidation(name, value);

        if (rawBody is not null)
        {
            request.Content = new ByteArrayContent(rawBody);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }
        else if (body is not null)
        {
            var json = body as string ?? JsonSerializer.Serialize(body, JsonDefaults.Options);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        using var response = await http.SendAsync(request);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        var result = new ApiResponse(response.StatusCode, bytes, response.Headers, response.Content.Headers);
        foreach (var setCookie in result.SetCookies) ApplySetCookie(setCookie);
        return result;
    }

    private void ApplySetCookie(string header)
    {
        var parts = header.Split(';', StringSplitOptions.TrimEntries);
        var eq = parts[0].IndexOf('=');
        if (eq <= 0) return;
        var name = parts[0][..eq];
        var value = parts[0][(eq + 1)..];
        var expired = parts.Skip(1).Any(p => p.StartsWith("expires=", StringComparison.OrdinalIgnoreCase) &&
            DateTimeOffset.TryParse(p["expires=".Length..], out var at) && at < DateTimeOffset.UtcNow);
        if (value.Length == 0 || expired) Cookies.Remove(name);
        else Cookies[name] = value;
    }

    public async Task FetchCsrfAsync()
    {
        var response = (await GetAsync("/api/auth/csrf")).ShouldBe(HttpStatusCode.OK);
        CsrfToken = response.Json["token"]!.GetValue<string>();
    }

    public async Task<Guid> RegisterAsync(string email, string password = TestUsers.Password, string? displayName = null)
    {
        if (CsrfToken is null) await FetchCsrfAsync();
        var response = (await PostAsync("/api/auth/register", new { email, password, displayName = displayName ?? "Demo " + email.Split('@')[0] }))
            .ShouldBe(HttpStatusCode.Created);
        return response.Json["id"]!.GetValue<Guid>();
    }

    /// <summary>Saat ilerletildiği için oturumu dolan kullanıcıyı yeniden giriş yaptırır.</summary>
    public async Task ReloginAsync(string email, string password = TestUsers.Password)
    {
        Cookies.Clear();
        CsrfToken = null;
        (await LoginAsync(email, password)).ShouldBe(HttpStatusCode.OK);
    }

    public async Task<ApiResponse> LoginAsync(string email, string password = TestUsers.Password)
    {
        if (CsrfToken is null) await FetchCsrfAsync();
        var response = await PostAsync("/api/auth/login", new { email, password });
        if (response.Status == HttpStatusCode.OK)
            CsrfToken = response.Json["csrfToken"]!.GetValue<string>();
        return response;
    }
}

public static class TestUsers
{
    /// <summary>Yalnız tek kullanımlık test veritabanlarında kullanılan sentetik parola.</summary>
    public const string Password = "Deneme-Parola-2026";
}

/// <summary>Her test kendi veritabanını ve host'unu alır.</summary>
public abstract class IntegrationTest(PostgresFixture postgres) : IAsyncLifetime
{
    protected PostgresFixture Postgres { get; } = postgres;
    protected TestDatabase Database { get; private set; } = null!;
    protected ManualClock Clock { get; } = new();
    protected LocaFactory Factory { get; private set; } = null!;

    protected virtual IDictionary<string, string?>? Settings => null;
    protected virtual void ConfigureServices(IServiceCollection services) { }
    protected virtual bool UseManualClock => true;

    public virtual async ValueTask InitializeAsync()
    {
        Database = await Postgres.CreateDatabaseAsync();
        Factory = new LocaFactory(Database, UseManualClock ? Clock : null, Settings, ConfigureServices);
    }

    public virtual async ValueTask DisposeAsync()
    {
        await Factory.DisposeAsync();
        // Her testin kendi veritabanı (ve havuzu) vardır; boşta bağlantılar sunucu sınırını doldurmasın.
        using (var pooled = new NpgsqlConnection(Database.AppConnectionString))
            NpgsqlConnection.ClearPool(pooled);
        GC.SuppressFinalize(this);
    }

    /// <summary>Kayıt olup giriş yapmış istemci.</summary>
    protected async Task<(TestClient Client, Guid UserId)> SignedInAsync(string email)
    {
        var client = Factory.NewClient();
        var id = await client.RegisterAsync(email);
        (await client.LoginAsync(email)).ShouldBe(HttpStatusCode.OK);
        return (client, id);
    }

    /// <summary>Her biri ayrı hesapla giriş yapmış müşteri istemcileri.</summary>
    protected async Task<List<TestClient>> CustomersAsync(int count, string prefix = "musteri")
    {
        var clients = new List<TestClient>(count);
        for (var i = 0; i < count; i++)
            clients.Add((await SignedInAsync($"{prefix}-{i:D2}@loca.test")).Client);
        return clients;
    }

    protected TestFailpoints Failpoints => Factory.Failpoints;
    protected ProviderFaults ProviderFaults => Factory.ProviderFaults;

    protected Task<int> RunOutboxAsync() => Factory.WithScopeAsync(sp => sp.GetRequiredService<OutboxProcessor>().RunOnceAsync(CancellationToken.None));
    protected Task<int> RunPspSimulatorAsync() => Factory.WithScopeAsync(sp => sp.GetRequiredService<PspSimProcessor>().RunOnceAsync(CancellationToken.None));
    protected Task<int> RunReconciliationAsync() => Factory.WithScopeAsync(sp => sp.GetRequiredService<PaymentReconciliationProcessor>().RunOnceAsync(CancellationToken.None));
    protected Task<int> RunSessionCancellationAsync() => Factory.WithScopeAsync(sp => sp.GetRequiredService<SessionCancellationProcessor>().RunOnceAsync(CancellationToken.None));
    protected Task<int> RunHoldExpiryAsync() => Factory.WithScopeAsync(sp => sp.GetRequiredService<HoldExpiryProcessor>().RunOnceAsync(CancellationToken.None));

    /// <summary>Şu anda (saat ilerletilmeden) yapılabilecek tüm arka plan işlerini bitirir: outbox + simülatör.</summary>
    protected async Task DrainAsync()
    {
        for (var i = 0; i < 25; i++)
            if (await RunOutboxAsync() + await RunPspSimulatorAsync() == 0) return;
        throw new InvalidOperationException("Background work did not settle.");
    }

    protected Task<string?> ScalarAsync(string sql, params NpgsqlParameter[] parameters) => Database.ScalarAsAdminAsync<string>(sql, parameters);
    protected Task<long> CountAsync(string sql, params NpgsqlParameter[] parameters) => Database.ScalarAsAdminAsync<long>(sql, parameters);

    /// <summary>Platform yöneticisi yalnız veritabanı düzeyinde atanabilir; testte de böyle yapılır.</summary>
    protected async Task<TestClient> SignedInPlatformAdminAsync(string email = "admin@loca.test")
    {
        var client = Factory.NewClient();
        var id = await client.RegisterAsync(email);
        await Database.ExecuteAsAdminAsync("UPDATE identity.users SET is_platform_admin = true WHERE id = @id", new NpgsqlParameter("id", id));
        (await client.LoginAsync(email)).ShouldBe(HttpStatusCode.OK);
        return client;
    }

    /// <summary>Onaylanmış (Active) organizasyon ve onun OrganizerAdmin'i.</summary>
    protected async Task<(TestClient Client, Guid UserId, Guid OrganizationId)> OrganizerAsync(string email, string organizationName, bool approve = true)
    {
        var (client, userId) = await SignedInAsync(email);
        var created = (await client.PostAsync("/api/organizations", new { name = organizationName })).ShouldBe(HttpStatusCode.Created);
        var organizationId = created.Json["id"]!.GetValue<Guid>();
        if (approve)
            await Database.ExecuteAsAdminAsync("UPDATE identity.organizations SET status = 'Active' WHERE id = @id", new NpgsqlParameter("id", organizationId));
        return (client, userId, organizationId);
    }
}
