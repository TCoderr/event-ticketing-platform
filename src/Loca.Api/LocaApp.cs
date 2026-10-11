using Loca.Api.Infrastructure.Auth;
using Loca.Api.Infrastructure.Config;
using Loca.Api.Infrastructure.Http;
using Loca.Api.Infrastructure.Idempotency;
using Loca.Api.Infrastructure.Metrics;
using Loca.Api.Infrastructure.Outbox;
using Loca.Api.Infrastructure.Persistence;
using Loca.Api.Infrastructure.RateLimiting;
using Loca.Api.Infrastructure.Time;
using Loca.Api.Infrastructure.Tx;
using Loca.Api.Infrastructure.Workers;
using Loca.Api.Modules.Catalog;
using Loca.Api.Modules.Identity;
using Loca.Api.Modules.Inventory;
using Loca.Api.Modules.Operations;
using Loca.Api.Modules.Orders;
using Loca.Api.Modules.Tickets;
using Loca.Api.PspSimulator;
using Loca.Domain;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Loca.Api;

public static class LocaApp
{
    public static void ConfigureServices(WebApplicationBuilder builder)
    {
        var services = builder.Services;
        var config = builder.Configuration;

        services.Configure<DatabaseOptions>(config.GetSection(DatabaseOptions.Section));
        services.Configure<AuthSessionOptions>(config.GetSection(AuthSessionOptions.Section));
        services.Configure<TransactionOptions>(config.GetSection(TransactionOptions.Section));
        services.Configure<RateLimitOptions>(config.GetSection(RateLimitOptions.Section));
        services.Configure<DemoOptions>(config.GetSection(DemoOptions.Section));
        services.Configure<HoldOptions>(config.GetSection(HoldOptions.Section));
        services.Configure<PolicyOptions>(config.GetSection(PolicyOptions.Section));
        services.Configure<IdempotencyOptions>(config.GetSection(IdempotencyOptions.Section));
        services.Configure<WorkerOptions>(config.GetSection(WorkerOptions.Section));
        services.Configure<OutboxOptions>(config.GetSection(OutboxOptions.Section));
        services.Configure<CancellationOptions>(config.GetSection(CancellationOptions.Section));
        services.Configure<RetentionOptions>(config.GetSection(RetentionOptions.Section));
        services.Configure<DemoCrowdOptions>(config.GetSection(DemoCrowdOptions.Section));
        services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(20));
        services.Configure<PspOptions>(config.GetSection(PspOptions.Section));
        services.Configure<PspSimulatorOptions>(config.GetSection(PspSimulatorOptions.Section));

        ConfigureKestrel(builder);

        services.AddDbContext<AppDbContext>((sp, options) =>
        {
            var database = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<DatabaseOptions>>().Value;
            AppDbContext.Configure(options, database.BuildAppConnectionString());
        });

        services.AddHttpContextAccessor();
        services.AddScoped<IClock, DbClock>();
        services.AddScoped<ITransactionRunner, TransactionRunner>();
        services.AddScoped<ICurrentUser, HttpCurrentUser>();
        services.AddScoped<ICorrelationContext, HttpCorrelationContext>();
        services.AddScoped<IAuditWriter, AuditWriter>();
        services.AddScoped<ISessionService, SessionService>();
        services.AddScoped<IOrganizationAccess, OrganizationAccess>();
        services.AddScoped<SessionCookieEvents>();
        services.AddSingleton<ReadinessProbe>();
        services.AddScoped<DemoSeeder>();
        services.AddScoped<IDemoSeedContributor, CatalogDemoSeed>();
        services.AddScoped<CatalogAccess>();
        services.AddScoped<IInventoryGuards, InventoryGuards>();
        services.AddScoped<InventoryService>();
        services.AddScoped<HoldService>();
        services.AddScoped<IIdempotentEndpoint, IdempotentEndpoint>();
        services.TryAddSingleton<IFailpoints, NoFailpoints>();
        services.AddScoped<IHoldOrderLookup, HoldOrderLookup>();
        services.AddScoped<IReservationLifecycleHook, OrderReservationHook>();
        services.AddScoped<IOutbox, OutboxWriter>();
        services.AddScoped<OrderService>();
        services.AddScoped<PaymentService>();
        services.AddScoped<IPaymentProvider, HttpPaymentProvider>();
        services.AddScoped<IOutboxHandler, PaymentInitiateHandler>();
        services.AddScoped<IOutboxHandler, RefundRequestedHandler>();
        services.AddScoped<IOutboxHandler, OrderPaidHandler>();
        services.AddScoped<TicketService>();
        services.AddScoped<CheckInService>();
        services.AddScoped<GateAccess>();
        services.AddSingleton<TicketTokenProtector>();
        services.AddHttpClient(PspOptions.HttpClientName);
        services.AddHttpClient(PspSimulatorOptions.WebhookClientName);
        services.AddProcessorWorker<OutboxProcessor>();
        services.AddProcessorWorker<PaymentReconciliationProcessor>();
        services.AddScoped<CancellationService>();
        services.AddProcessorWorker<SessionCancellationProcessor>();
        services.AddSingleton<RetentionSchedule>();
        services.AddSingleton<AppMetrics>();
        services.AddScoped<OperationsQueries>();
        services.AddScoped<PspSimulatorPanel>();
        services.AddSingleton<DemoCrowdSchedule>();
        services.AddScoped<IDemoSeedContributor, DemoCrowdSeed>();
        services.AddScoped<IDemoSeedContributor, GateDemoSeed>();
        services.AddProcessorWorker<DemoCrowdProcessor>();
        services.AddProcessorWorker<RetentionProcessor>();

        // Simüle PSP: ayrı DbContext/şema; uygulama modülleri bu bağlama erişmez.
        services.AddDbContext<PspSimDbContext>((sp, options) =>
        {
            var database = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<DatabaseOptions>>().Value;
            PspSimDbContext.Configure(options, database.BuildAppConnectionString());
        });
        services.AddProcessorWorker<PspSimProcessor>();
        services.AddSingleton<TransactionStats>();
        services.AddProcessorWorker<HoldExpiryProcessor>();

        services.AddIdentityCore<AppUser>(o =>
            {
                o.User.RequireUniqueEmail = true;
                o.Password.RequiredLength = 10;
                o.Password.RequireDigit = true;
                o.Password.RequireLowercase = true;
                o.Password.RequireUppercase = false;
                o.Password.RequireNonAlphanumeric = false;
                o.Lockout.AllowedForNewUsers = true;
                o.Lockout.MaxFailedAccessAttempts = 5;
                o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
            })
            .AddEntityFrameworkStores<AppDbContext>();

        services.AddAuthentication(AuthConstants.Scheme)
            .AddCookie(AuthConstants.Scheme, o =>
            {
                o.Cookie.Name = AuthConstants.CookieName;
                o.Cookie.HttpOnly = true;
                o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                o.Cookie.SameSite = SameSiteMode.Lax;
                o.Cookie.Path = "/";
                o.Cookie.IsEssential = true;
                o.SlidingExpiration = false;
                o.EventsType = typeof(SessionCookieEvents);
            });
        // Ayarlar tembel bağlanır: test host'unun yapılandırma geçersiz kılmaları Build sırasında eklenir.
        services.AddOptions<Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationOptions>(AuthConstants.Scheme)
            .Configure<Microsoft.Extensions.Options.IOptions<AuthSessionOptions>>((cookie, session) =>
                cookie.ExpireTimeSpan = TimeSpan.FromMinutes(session.Value.AbsoluteLifetimeMinutes));
        services.AddAuthorization();

        services.AddAntiforgery(o =>
        {
            o.HeaderName = AuthConstants.CsrfHeaderName;
            o.Cookie.Name = AuthConstants.CsrfCookieName;
            o.Cookie.HttpOnly = true;
            o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            o.Cookie.SameSite = SameSiteMode.Strict;
            o.Cookie.Path = "/";
            o.Cookie.IsEssential = true;
        });

        var dataProtection = services.AddDataProtection().SetApplicationName("loca");
        var keysDirectory = config["DataProtection:KeysDirectory"];
        if (!string.IsNullOrWhiteSpace(keysDirectory))
            dataProtection.PersistKeysToFileSystem(new DirectoryInfo(keysDirectory));

        services.AddRateLimiter(_ => { });
        services.AddOptions<Microsoft.AspNetCore.RateLimiting.RateLimiterOptions>()
            .Configure<Microsoft.Extensions.Options.IOptions<RateLimitOptions>>((limiter, limits) =>
                RateLimitPolicies.Configure(limiter, limits.Value));

        services.ConfigureHttpJsonOptions(o => JsonDefaults.Apply(o.SerializerOptions));
        // Bağlama hataları (bozuk JSON, bilinmeyen alan) çıplak 400 yerine istisna olarak yükselsin ki
        // tek hata biçimi (ProblemDetails + code) uygulanabilsin.
        services.Configure<Microsoft.AspNetCore.Routing.RouteHandlerOptions>(o => o.ThrowOnBadRequest = true);
        services.AddExceptionHandler<GlobalExceptionHandler>();
        services.AddProblemDetails();
        services.AddOpenApi();
    }

    public static void ConfigurePipeline(WebApplication app)
    {
        // Kapalı başarısızlık: üretim kipinde ödeme sağlayıcısı yapılandırması (adres, API anahtarı, webhook secret'ı)
        // eksikse uygulama hiç başlamaz. Varsayılan/gömülü secret yoktur.
        if (app.Environment.IsProduction() &&
            !app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<PspOptions>>().Value.IsConfigured)
            throw new InvalidOperationException(
                "Payment provider configuration is incomplete: Psp:BaseUrl, Psp:ApiKeyFile and at least one Psp:WebhookSecretFiles entry are required.");

        app.UseMiddleware<CorrelationIdMiddleware>();
        app.UseExceptionHandler();
        app.UseMiddleware<SecurityHeadersMiddleware>();
        if (!app.Environment.IsDevelopment()) app.UseHsts();

        var hasSpa = File.Exists(Path.Combine(app.Environment.WebRootPath ?? "", "index.html"));
        if (hasSpa)
        {
            app.UseDefaultFiles();
            app.UseStaticFiles(new StaticFileOptions
            {
                OnPrepareResponse = ctx =>
                {
                    // Vite çıktısı içerik özetli dosya adları kullanır; index.html her zaman yeniden doğrulanır.
                    var isHashedAsset = ctx.Context.Request.Path.StartsWithSegments("/assets");
                    ctx.Context.Response.Headers.CacheControl = isHashedAsset ? "public, max-age=31536000, immutable" : "no-cache";
                },
            });
        }

        app.UseRouting();
        app.UseMiddleware<AccessLogMiddleware>();
        app.UseAuthentication();
        app.UseRateLimiter();
        app.UseAuthorization();
        app.UseMiddleware<CsrfMiddleware>();

        MapEndpoints(app);

        // Bilinmeyen API yolu SPA'ya düşmez; JSON 404 döner.
        app.Map("/api/{**rest}", (HttpContext http) =>
            ProblemWriter.WriteAsync(http, StatusCodes.Status404NotFound, ErrorCodes.NotFound, "No such API endpoint."));

        if (hasSpa)
            app.MapFallbackToFile("index.html", new StaticFileOptions
            {
                OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache",
            });
    }

    private static void MapEndpoints(WebApplication app)
    {
        app.MapGet("/health/live", () => Results.Ok(new { status = "live" })).ExcludeFromDescription();
        app.MapGet("/health/ready", async (ReadinessProbe probe, CancellationToken ct) =>
        {
            var (ready, reason) = await probe.CheckAsync(ct);
            return ready
                ? Results.Ok(new { status = "ready" })
                : Results.Json(new { status = "not_ready", reason }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }).ExcludeFromDescription();

        app.MapOpenApi("/openapi/{documentName}.json");

        AuthEndpoints.Map(app);
        OrganizationEndpoints.Map(app);
        OrganizerCatalogEndpoints.Map(app);
        PublicCatalogEndpoints.Map(app);
        HoldEndpoints.Map(app);
        OrderEndpoints.Map(app);
        WebhookEndpoint.Map(app);
        TicketEndpoints.Map(app);
        CancellationEndpoints.Map(app);
        OperationsEndpoints.Map(app);
        if (app.Configuration.GetValue<bool>($"{PspSimulatorOptions.Section}:Enabled"))
            PspSimulatorEndpoints.Map(app);
    }

    private static void ConfigureKestrel(WebApplicationBuilder builder)
    {
        var hosting = builder.Configuration.GetSection(HostingOptions.Section).Get<HostingOptions>() ?? new HostingOptions();
        if (hosting.HttpsPort <= 0) return;

        var localTls = builder.Configuration.GetSection(LocalTlsOptions.Section).Get<LocalTlsOptions>() ?? new LocalTlsOptions();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxRequestBodySize = 256 * 1024;
            kestrel.ListenAnyIP(hosting.HttpsPort, listen =>
            {
                if (localTls.AutoGenerate)
                    listen.UseHttps(LocalCertificate.LoadOrCreate(localTls.Directory));
                else if (!string.IsNullOrWhiteSpace(hosting.CertificatePfxPath))
                    listen.UseHttps(hosting.CertificatePfxPath, SecretFile.ReadOptional(hosting.CertificatePasswordFile));
                else
                    throw new InvalidOperationException("Hosting:HttpsPort is set but no certificate is configured (Hosting:CertificatePfxPath or LocalTls:AutoGenerate).");
            });
            if (hosting.InternalHttpPort > 0)
                kestrel.ListenLocalhost(hosting.InternalHttpPort);
        });
    }
}
