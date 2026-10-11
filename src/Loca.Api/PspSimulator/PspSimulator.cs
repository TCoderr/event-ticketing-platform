using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Loca.Api.Infrastructure.Time;
using Loca.Api.Infrastructure.Workers;
using Loca.Api.Modules.Orders;
using Loca.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Options;

namespace Loca.Api.PspSimulator;

// Simüle ödeme sağlayıcısı (ADR 0007). Uygulamayla aynı süreçte barınır ama ayrı bir sistem gibi davranır:
// kendi şeması/DbContext'i vardır, uygulama onu yalnız HTTP ile çağırır, o da uygulamaya yalnız imzalı
// webhook ile döner. Uygulama modülleri bu şemaya erişmez. Model: anında tahsilat; iptal = iade (void yok).

public sealed class SimCharge
{
    public string Id { get; set; } = "";
    public string MerchantReference { get; set; } = "";
    public long AmountMinor { get; set; }
    public string Currency { get; set; } = "";
    public string MethodToken { get; set; } = "";

    /// <summary>unsubmitted | pending | succeeded | declined | failed</summary>
    public string Status { get; set; } = "";

    public string? DeclineCode { get; set; }
    public string? PendingOutcome { get; set; }
    public int Sequence { get; set; }
    public int CreateAttempts { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ResolveAt { get; set; }
}

public sealed class SimRefund
{
    public string Id { get; set; } = "";
    public string MerchantReference { get; set; } = "";
    public string ChargeId { get; set; } = "";
    public long AmountMinor { get; set; }

    /// <summary>unsubmitted | succeeded | failed</summary>
    public string Status { get; set; } = "";

    public string? FailureCode { get; set; }
    public int CreateAttempts { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class SimWebhookDelivery
{
    public Guid Id { get; set; }
    public long Ordinal { get; set; }
    public string EventId { get; set; } = "";
    public byte[] Body { get; set; } = [];
    public DateTimeOffset DeliverAt { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
    public int? LastStatus { get; set; }
}

public sealed class PspSimDbContext(DbContextOptions<PspSimDbContext> options) : DbContext(options)
{
    public const string Schema = "pspsim";
    public const string MigrationsHistoryTable = "__ef_migrations_history";

    public DbSet<SimCharge> Charges => Set<SimCharge>();
    public DbSet<SimRefund> Refunds => Set<SimRefund>();
    public DbSet<SimWebhookDelivery> Deliveries => Set<SimWebhookDelivery>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema(Schema);
        b.Entity<SimCharge>(e =>
        {
            e.ToTable("charges");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(40);
            e.Property(x => x.MerchantReference).HasMaxLength(64);
            e.Property(x => x.Currency).HasMaxLength(3);
            e.Property(x => x.MethodToken).HasMaxLength(40);
            e.Property(x => x.Status).HasMaxLength(16);
            e.Property(x => x.DeclineCode).HasMaxLength(40);
            e.Property(x => x.PendingOutcome).HasMaxLength(16);
            e.HasIndex(x => x.MerchantReference).IsUnique();
        });
        b.Entity<SimRefund>(e =>
        {
            e.ToTable("refunds");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(40);
            e.Property(x => x.MerchantReference).HasMaxLength(64);
            e.Property(x => x.ChargeId).HasMaxLength(40);
            e.Property(x => x.Status).HasMaxLength(16);
            e.Property(x => x.FailureCode).HasMaxLength(40);
            e.HasIndex(x => x.MerchantReference).IsUnique();
            e.HasIndex(x => x.ChargeId).IsUnique(); // Tahsilat başına tek (tam) iade.
        });
        b.Entity<SimWebhookDelivery>(e =>
        {
            e.ToTable("webhook_deliveries");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Ordinal).UseIdentityAlwaysColumn();
            e.Property(x => x.EventId).HasMaxLength(80);
            e.HasIndex(x => new { x.DeliverAt, x.Ordinal }).HasFilter("delivered_at IS NULL");
        });
    }

    public static void Configure(DbContextOptionsBuilder builder, string connectionString) => builder
        .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable(MigrationsHistoryTable, Schema).CommandTimeout(15))
        .UseSnakeCaseNamingConvention();
}

internal sealed class DesignTimePspSimDbContextFactory : IDesignTimeDbContextFactory<PspSimDbContext>
{
    public PspSimDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<PspSimDbContext>();
        PspSimDbContext.Configure(builder, Environment.GetEnvironmentVariable("LOCA_DESIGN_CONNECTION")
            ?? "Host=127.0.0.1;Port=1;Database=design_time_only;Username=design_time_only");
        return new PspSimDbContext(builder.Options);
    }
}

public sealed class PspSimulatorOptions
{
    public const string Section = "Psp:Simulator";
    public const string WebhookClientName = "psp-sim-webhook";

    public bool Enabled { get; set; }
    public string WebhookTargetUrl { get; set; } = "";
    public int AsyncDelaySeconds { get; set; } = 2;
    public int LateSuccessDelaySeconds { get; set; } = 900;
    public int WebhookMaxAttempts { get; set; } = 6;

    /// <summary>Webhook'u hangi aktif secret ile imzalayacağı (secret rotasyonunu denemek için).</summary>
    public int SigningSecretIndex { get; set; }
}

public sealed record SimChargeRequest(string MerchantReference, long AmountMinor, string Currency, string MethodToken);
public sealed record SimRefundRequest(string MerchantReference, string ChargeReference, long AmountMinor);
public sealed record SimResolveRequest(string Outcome);
public sealed record SimChargeDto(string Id, string MerchantReference, string Status, string? DeclineCode, int Sequence, long AmountMinor, string Currency);
public sealed record SimRefundDto(string Id, string MerchantReference, string Status, string? FailureCode, long AmountMinor);

internal static class PspSimulatorEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/sim-psp/v1").AllowAnonymous().ExcludeFromDescription().AddEndpointFilter(async (context, next) =>
        {
            // Yerel stack'te simülatöre yalnız süreç içi (loopback) dinleyiciden ulaşılır; yayınlanan HTTPS portundan
            // gelen istek için bu uçlar "yok" gibidir. (Test host'unda iç dinleyici yoktur; kısıt uygulanmaz.)
            var internalPort = context.HttpContext.RequestServices.GetRequiredService<IConfiguration>().GetValue<int>("Hosting:InternalHttpPort");
            if (internalPort > 0 && context.HttpContext.Connection.LocalPort != internalPort)
                return Results.Json(new { error = "not_found" }, statusCode: StatusCodes.Status404NotFound);

            // Sağlayıcı API anahtarı: uygulamanın çerez oturumuyla ilgisi yoktur.
            var options = context.HttpContext.RequestServices.GetRequiredService<IOptions<PspOptions>>().Value;
            var expected = options.ResolveApiKey();
            var header = context.HttpContext.Request.Headers.Authorization.ToString();
            var presented = header.StartsWith("Bearer ", StringComparison.Ordinal) ? header["Bearer ".Length..] : "";
            if (expected is null || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(expected)))
                return Results.Json(new { error = "unauthorized" }, statusCode: StatusCodes.Status401Unauthorized);
            return await next(context);
        });

        api.MapPost("/charges", CreateChargeAsync);
        api.MapGet("/charges/by-reference/{reference}", async (string reference, PspSimDbContext db, CancellationToken ct) =>
            await db.Charges.AsNoTracking().FirstOrDefaultAsync(c => c.MerchantReference == reference && c.Status != "unsubmitted", ct) is { } charge
                ? Results.Json(ToDto(charge))
                : Results.Json(new { error = "not_found" }, statusCode: StatusCodes.Status404NotFound));
        api.MapGet("/charges", async (string? status, PspSimDbContext db, CancellationToken ct) =>
            Results.Json((await db.Charges.AsNoTracking().Where(c => status == null || c.Status == status).OrderByDescending(c => c.CreatedAt).Take(100).ToListAsync(ct)).Select(ToDto)));
        api.MapPost("/charges/by-reference/{reference}/resolve", ResolveAsync);

        api.MapPost("/refunds", CreateRefundAsync);
        api.MapGet("/refunds/by-reference/{reference}", async (string reference, PspSimDbContext db, CancellationToken ct) =>
            await db.Refunds.AsNoTracking().FirstOrDefaultAsync(r => r.MerchantReference == reference && r.Status != "unsubmitted", ct) is { } refund
                ? Results.Json(ToDto(refund))
                : Results.Json(new { error = "not_found" }, statusCode: StatusCodes.Status404NotFound));
    }

    private static SimChargeDto ToDto(SimCharge c) => new(c.Id, c.MerchantReference, c.Status, c.DeclineCode, c.Sequence, c.AmountMinor, c.Currency);
    private static SimRefundDto ToDto(SimRefund r) => new(r.Id, r.MerchantReference, r.Status, r.FailureCode, r.AmountMinor);
    private static IResult ChargeResult(SimCharge c) => Results.Json(ToDto(c), statusCode: c.Status == "pending" ? StatusCodes.Status202Accepted : StatusCodes.Status200OK);
    private static string NewId(string prefix) => prefix + RandomNumberGenerator.GetHexString(24, lowercase: true);

    private static async Task<IResult> CreateChargeAsync(SimChargeRequest request, HttpContext http, PspSimDbContext db, IClock clock,
        IOptions<PspSimulatorOptions> options, WorkerSignal<PspSimProcessor> signal, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.MerchantReference) || request.MerchantReference.Length > 64 || request.AmountMinor < 0 ||
            !Money.IsSupportedCurrency(request.Currency) || !PaymentMethods.All.Contains(request.MethodToken ?? ""))
            return Results.Json(new { error = "invalid_request" }, statusCode: StatusCodes.Status400BadRequest);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = await clock.NowAsync(ct);

        // Referansla idempotent: eşzamanlı aynı referans, unique indeks üzerinde sıraya girer.
        var id = NewId("ch_");
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO pspsim.charges (id, merchant_reference, amount_minor, currency, method_token, status, sequence, create_attempts, created_at)
            VALUES ({id}, {request.MerchantReference}, {request.AmountMinor}, {request.Currency}, {request.MethodToken}, 'unsubmitted', 0, 0, {now})
            ON CONFLICT (merchant_reference) DO NOTHING
            """, ct);
        var charge = await db.Charges.FromSql($"SELECT * FROM pspsim.charges WHERE merchant_reference = {request.MerchantReference} FOR UPDATE").SingleAsync(ct);
        if (charge.AmountMinor != request.AmountMinor || charge.Currency != request.Currency)
            return Results.Json(new { error = "reference_reused_with_different_amount" }, statusCode: StatusCodes.Status409Conflict);

        charge.CreateAttempts++;
        var dropResponse = false;
        if (charge.Status == "unsubmitted")
        {
            var opts = options.Value;
            switch (charge.MethodToken)
            {
                case PaymentMethods.Decline:
                    Finalize(charge, "declined", "insufficient_funds");
                    break;
                case PaymentMethods.Transient when charge.CreateAttempts == 1:
                    // İlk deneme: sağlayıcı geçici hata verir ve hiçbir tahsilat kaydetmez.
                    await db.SaveChangesAsync(ct);
                    await tx.CommitAsync(ct);
                    return Results.Json(new { error = "temporarily_unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
                case PaymentMethods.TimeoutSuccess:
                    Finalize(charge, "succeeded", null);
                    dropResponse = true; // Tahsilat kaydedilir, yanıt kaybolur.
                    break;
                case PaymentMethods.TimeoutDecline:
                    Finalize(charge, "declined", "do_not_honor");
                    dropResponse = true;
                    break;
                case PaymentMethods.AsyncSuccess or PaymentMethods.AsyncDuplicate or PaymentMethods.AsyncOutOfOrder:
                    Pend(charge, "succeeded", now.AddSeconds(opts.AsyncDelaySeconds));
                    break;
                case PaymentMethods.AsyncDecline:
                    Pend(charge, "declined", now.AddSeconds(opts.AsyncDelaySeconds));
                    break;
                case PaymentMethods.LateSuccess:
                    Pend(charge, "succeeded", now.AddSeconds(opts.LateSuccessDelaySeconds));
                    break;
                default:
                    Finalize(charge, "succeeded", null);
                    break;
            }
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        signal.Notify();

        if (dropResponse)
        {
            // Sınır dışı hata: işlem commit oldu ama istemci hiçbir yanıt alamaz (bağlantı kesilir).
            http.Abort();
            return Results.Empty;
        }
        return ChargeResult(charge);

        static void Finalize(SimCharge c, string status, string? declineCode)
        {
            c.Status = status;
            c.DeclineCode = declineCode;
            c.Sequence = 1;
        }

        static void Pend(SimCharge c, string outcome, DateTimeOffset resolveAt)
        {
            c.Status = "pending";
            c.PendingOutcome = outcome;
            c.Sequence = 1;
            c.ResolveAt = resolveAt;
        }
    }

    /// <summary>Sağlayıcı panelinin eşdeğeri: bekleyen tahsilatı elle sonuçlandırır (demo ve testler için).</summary>
    private static async Task<IResult> ResolveAsync(string reference, SimResolveRequest request, PspSimDbContext db, IClock clock,
        WorkerSignal<PspSimProcessor> signal, CancellationToken ct)
    {
        if (request.Outcome is not ("succeeded" or "declined"))
            return Results.Json(new { error = "invalid_outcome" }, statusCode: StatusCodes.Status400BadRequest);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var charge = await db.Charges.FromSql($"SELECT * FROM pspsim.charges WHERE merchant_reference = {reference} FOR UPDATE").SingleOrDefaultAsync(ct);
        if (charge is null) return Results.Json(new { error = "not_found" }, statusCode: StatusCodes.Status404NotFound);
        if (charge.Status == "pending")
        {
            charge.PendingOutcome = request.Outcome;
            PspSimProcessor.Resolve(db, charge, await clock.NowAsync(ct));
            await db.SaveChangesAsync(ct);
        }
        await tx.CommitAsync(ct);
        signal.Notify();
        return Results.Json(ToDto(charge));
    }

    private static async Task<IResult> CreateRefundAsync(SimRefundRequest request, HttpContext http, PspSimDbContext db, IClock clock, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.MerchantReference) || request.MerchantReference.Length > 64 || string.IsNullOrWhiteSpace(request.ChargeReference))
            return Results.Json(new { error = "invalid_request" }, statusCode: StatusCodes.Status400BadRequest);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var charge = await db.Charges.FromSql($"SELECT * FROM pspsim.charges WHERE merchant_reference = {request.ChargeReference} FOR UPDATE").SingleOrDefaultAsync(ct);
        if (charge is null || charge.Status != "succeeded")
            return Results.Json(new { error = "charge_not_refundable" }, statusCode: StatusCodes.Status422UnprocessableEntity);
        if (request.AmountMinor != charge.AmountMinor)
            return Results.Json(new { error = "partial_refund_not_supported" }, statusCode: StatusCodes.Status422UnprocessableEntity);

        var refund = await db.Refunds.FirstOrDefaultAsync(r => r.ChargeId == charge.Id, ct);
        if (refund is not null && refund.MerchantReference != request.MerchantReference)
            return Results.Json(new { error = "charge_already_refunded" }, statusCode: StatusCodes.Status409Conflict);
        if (refund is null)
        {
            refund = new SimRefund
            {
                Id = NewId("re_"), MerchantReference = request.MerchantReference, ChargeId = charge.Id, AmountMinor = charge.AmountMinor,
                Status = "unsubmitted", CreatedAt = await clock.NowAsync(ct),
            };
            db.Refunds.Add(refund);
        }

        refund.CreateAttempts++;
        var dropResponse = false;
        if (refund.Status == "unsubmitted")
        {
            switch (charge.MethodToken)
            {
                case PaymentMethods.RefundFail:
                    refund.Status = "failed";
                    refund.FailureCode = "refund_not_permitted";
                    break;
                case PaymentMethods.RefundTransient when refund.CreateAttempts <= 2:
                    await db.SaveChangesAsync(ct);
                    await tx.CommitAsync(ct);
                    return Results.Json(new { error = "temporarily_unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
                case PaymentMethods.RefundTimeout:
                    refund.Status = "succeeded";
                    dropResponse = true; // İade kaydedilir, yanıt kaybolur; aynı referansla tekrar ikinci iade yaratmaz.
                    break;
                default:
                    refund.Status = "succeeded";
                    break;
            }
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        if (dropResponse)
        {
            http.Abort();
            return Results.Empty;
        }
        return Results.Json(ToDto(refund));
    }
}

/// <summary>Simülatörün arka plan işi: zamanı gelen bekleyen tahsilatları sonuçlandırır ve webhook'ları teslim eder.</summary>
public sealed class PspSimProcessor(
    PspSimDbContext db, IClock clock, IHttpClientFactory clients, IOptions<PspSimulatorOptions> options, IOptions<PspOptions> psp,
    ILogger<PspSimProcessor> logger) : IBatchProcessor
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string Name => "psp-simulator";

    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        if (!options.Value.Enabled) return 0;
        return await ResolveDueAsync(ct) + await DeliverDueAsync(ct);
    }

    public async Task<int> ResolveDueAsync(CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = await clock.NowAsync(ct);
        var due = await db.Charges.FromSql($"""
            SELECT * FROM pspsim.charges WHERE status = 'pending' AND resolve_at <= {now} ORDER BY resolve_at, id LIMIT 20 FOR UPDATE SKIP LOCKED
            """).ToListAsync(ct);
        foreach (var charge in due) Resolve(db, charge, now);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return due.Count;
    }

    internal static void Resolve(PspSimDbContext db, SimCharge charge, DateTimeOffset now)
    {
        var outcome = charge.PendingOutcome ?? "succeeded";
        charge.Status = outcome;
        charge.DeclineCode = outcome == "declined" ? "issuer_declined" : null;
        charge.Sequence = 2;
        charge.ResolveAt = null;

        var final = Event(charge, $"charge.{outcome}", sequence: 2, now);
        switch (charge.MethodToken)
        {
            case PaymentMethods.AsyncDuplicate:
                // Aynı olay (aynı kimlik, aynı baytlar) iki kez teslim edilir.
                db.Deliveries.Add(Delivery(final, now));
                db.Deliveries.Add(Delivery(final, now));
                break;
            case PaymentMethods.AsyncOutOfOrder:
                // Sonuç olayı önce, daha eski "pending" olayı sonra teslim edilir.
                db.Deliveries.Add(Delivery(final, now));
                db.Deliveries.Add(Delivery(Event(charge, "charge.pending", sequence: 1, now), now.AddSeconds(1)));
                break;
            default:
                db.Deliveries.Add(Delivery(final, now));
                break;
        }
    }

    private static (string EventId, byte[] Body) Event(SimCharge charge, string type, int sequence, DateTimeOffset now)
    {
        var id = "evt_" + RandomNumberGenerator.GetHexString(24, lowercase: true);
        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            id, type, created = now.ToUnixTimeSeconds(), sequence,
            data = new
            {
                merchantReference = charge.MerchantReference, providerPaymentId = charge.Id, amountMinor = charge.AmountMinor,
                currency = charge.Currency, declineCode = type == "charge.declined" ? charge.DeclineCode : null,
            },
        }, Json);
        return (id, body);
    }

    private static SimWebhookDelivery Delivery((string EventId, byte[] Body) evt, DateTimeOffset deliverAt) =>
        new() { Id = Guid.CreateVersion7(), EventId = evt.EventId, Body = evt.Body, DeliverAt = deliverAt };

    public async Task<int> DeliverDueAsync(CancellationToken ct)
    {
        var opts = options.Value;
        var secrets = psp.Value.ResolveWebhookSecrets();
        if (string.IsNullOrWhiteSpace(opts.WebhookTargetUrl) || secrets.Count == 0) return 0;
        var secret = secrets[Math.Clamp(opts.SigningSecretIndex, 0, secrets.Count - 1)];

        // Teslim denemesi önce kaydedilir (bir sonraki deneme zamanı ileri alınır), HTTP çağrısı transaction dışındadır.
        List<SimWebhookDelivery> due;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            var now = await clock.NowAsync(ct);
            due = await db.Deliveries.FromSql($"""
                SELECT * FROM pspsim.webhook_deliveries
                WHERE delivered_at IS NULL AND deliver_at <= {now} AND attempts < {opts.WebhookMaxAttempts}
                ORDER BY deliver_at, ordinal LIMIT 20 FOR UPDATE SKIP LOCKED
                """).ToListAsync(ct);
            foreach (var delivery in due)
            {
                delivery.Attempts++;
                delivery.DeliverAt = now.AddSeconds(Math.Min(60, 2 * Math.Pow(2, delivery.Attempts)));
            }
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        var client = clients.CreateClient(PspSimulatorOptions.WebhookClientName);
        foreach (var delivery in due)
        {
            var sentAt = await clock.NowAsync(ct);
            using var request = new HttpRequestMessage(HttpMethod.Post, opts.WebhookTargetUrl) { Content = new ByteArrayContent(delivery.Body) };
            request.Content.Headers.ContentType = new("application/json");
            request.Headers.TryAddWithoutValidation(WebhookEndpoint.SignatureHeader, WebhookSignature.Sign(secret, sentAt.ToUnixTimeSeconds(), delivery.Body));
            int status;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                using var response = await client.SendAsync(request, timeout.Token);
                status = (int)response.StatusCode;
            }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                status = 0;
                logger.LogWarning("Simulated webhook delivery failed with {ExceptionType}; it will be retried.", e.GetType().Name);
            }

            // 2xx: teslim edildi. 409 (kimlik çakışması) ve 4xx: yeniden denemek sonucu değiştirmez.
            var done = status is >= 200 and < 300 or >= 400 and < 500;
            await db.Deliveries.Where(d => d.Id == delivery.Id).ExecuteUpdateAsync(u => u
                .SetProperty(d => d.LastStatus, status)
                .SetProperty(d => d.DeliveredAt, done ? sentAt : (DateTimeOffset?)null), ct);
        }
        return due.Count;
    }
}
