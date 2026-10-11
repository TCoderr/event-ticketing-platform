using System.Text.Json;
using Loca.Api.Infrastructure.Auth;
using Loca.Api.Infrastructure.Http;
using Loca.Api.Infrastructure.Persistence;
using Loca.Api.Infrastructure.Time;
using Loca.Api.Infrastructure.Tx;
using Loca.Domain;
using Microsoft.EntityFrameworkCore;

namespace Loca.Api.Infrastructure.Idempotency;

/// <summary>
/// Kalıcı idempotency kaydı (ADR 0003). Kapsam: (kullanıcı, operasyon, anahtar). Yanıt gövdesi bayt olarak
/// saklanır; jsonb bayt düzenini korumadığı için kullanılmaz. Hiçbir iş tablosuna FK vermez/almaz.
/// </summary>
public sealed class IdempotencyRecord
{
    public Guid UserId { get; set; }
    public string Operation { get; set; } = "";
    public string IdempotencyKey { get; set; } = "";
    public int HashVersion { get; set; }
    public string RequestHash { get; set; } = "";
    public int? ResponseStatus { get; set; }
    public string? ResponseContentType { get; set; }
    public string? ResponseLocation { get; set; }
    public byte[]? ResponseBody { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

internal static class IdempotencyModel
{
    public static void Configure(ModelBuilder b)
    {
        b.Entity<IdempotencyRecord>(e =>
        {
            e.ToTable("idempotency_records", "ops");
            e.HasKey(x => new { x.UserId, x.Operation, x.IdempotencyKey });
            e.Property(x => x.Operation).HasMaxLength(40);
            e.Property(x => x.IdempotencyKey).HasMaxLength(CanonicalRequest.MaxKeyLength);
            e.Property(x => x.RequestHash).HasMaxLength(64).IsFixedLength();
            e.Property(x => x.ResponseContentType).HasMaxLength(80);
            e.Property(x => x.ResponseLocation).HasMaxLength(200);
            e.HasIndex(x => x.ExpiresAt);
        });
    }
}

public sealed class IdempotencyOptions
{
    public const string Section = "Idempotency";
    public int RetentionHours { get; set; } = 24;
    public int MaxBodyBytes { get; set; } = 16 * 1024;
}

/// <summary>Gövdesiz idempotent istekler için.</summary>
public sealed record NoBody;

/// <summary>Başarılı (2xx) sonuç. Başarısızlıklar <see cref="ApiException"/> olarak fırlatılır ve saklanmaz.</summary>
public sealed record IdempotentResult(int StatusCode, object Body, string? Location = null);

public interface IFailpoints
{
    /// <summary>Üretimde hiçbir şey yapmaz. Test host'u belirli noktalarda bekletme/hata enjekte eder.</summary>
    ValueTask HitAsync(string name, CancellationToken ct = default);
}

internal sealed class NoFailpoints : IFailpoints
{
    public ValueTask HitAsync(string name, CancellationToken ct = default) => ValueTask.CompletedTask;
}

public interface IIdempotentEndpoint
{
    /// <param name="authorize">
    /// Kayıtlı yanıt oynatılmadan ÖNCE çalışır: güncel sahiplik/rol kontrolü burada yapılır, böylece
    /// yetkisi kaldırılmış bir çağıran eski başarılı yanıtı alamaz.
    /// </param>
    Task<IResult> RunAsync<TRequest>(HttpContext http, string operation,
        Func<TRequest, CancellationToken, Task<IdempotentResult>> handler,
        Func<CancellationToken, Task>? authorize = null) where TRequest : class;
}

internal sealed class IdempotentEndpoint(
    AppDbContext db, ICurrentUser currentUser, IClock clock, ITransactionRunner transactions, IFailpoints failpoints,
    Microsoft.Extensions.Options.IOptions<IdempotencyOptions> options) : IIdempotentEndpoint
{
    public const string KeyHeader = "Idempotency-Key";
    public const string ReplayHeader = "Idempotent-Replayed";
    private const string JsonContentType = "application/json; charset=utf-8";

    public async Task<IResult> RunAsync<TRequest>(HttpContext http, string operation,
        Func<TRequest, CancellationToken, Task<IdempotentResult>> handler,
        Func<CancellationToken, Task>? authorize = null) where TRequest : class
    {
        var ct = http.RequestAborted;
        var userId = currentUser.RequireUserId();

        var key = http.Request.Headers[KeyHeader].ToString();
        if (string.IsNullOrEmpty(key))
            throw ApiErrors.BadRequest(ErrorCodes.IdempotencyKeyRequired, $"This operation requires an {KeyHeader} header.");
        if (!CanonicalRequest.IsValidKey(key))
            throw ApiErrors.BadRequest(ErrorCodes.IdempotencyKeyInvalid,
                $"{KeyHeader} must be {CanonicalRequest.MinKeyLength}-{CanonicalRequest.MaxKeyLength} characters of [A-Za-z0-9_.:-].");

        var raw = await RequestBody.ReadRawAsync(http, options.Value.MaxBodyBytes, ct);
        string hash;
        try
        {
            hash = CanonicalRequest.ComputeHash(http.Request.Method, http.Request.Path.Value ?? "", raw);
        }
        catch (JsonException)
        {
            throw ApiErrors.BadRequest(ErrorCodes.ValidationFailed, "The request body is not valid JSON.");
        }

        var request = typeof(TRequest) == typeof(NoBody)
            ? ParseNoBody<TRequest>(raw)
            : RequestBody.Deserialize<TRequest>(raw);

        if (authorize is not null) await authorize(ct);

        var outcome = await transactions.RunAsync(async token =>
        {
            var now = await clock.NowAsync(token);
            for (var attempt = 0; ; attempt++)
            {
                var expiresAt = now.AddHours(options.Value.RetentionHours);
                // Unique ihlali istisnası üretilmez: çakışmada 0 satır döner ve transaction kullanılabilir kalır.
                // Aynı anahtarla eşzamanlı ikinci istek burada, ilkinin commit/rollback'ini bekler.
                var claimed = await db.Database.ExecuteSqlAsync($"""
                    INSERT INTO ops.idempotency_records (user_id, operation, idempotency_key, hash_version, request_hash, created_at, expires_at)
                    VALUES ({userId}, {operation}, {key}, {CanonicalRequest.HashVersion}, {hash}, {now}, {expiresAt})
                    ON CONFLICT (user_id, operation, idempotency_key) DO NOTHING
                    """, token);
                if (claimed == 1) break;
                if (attempt >= 3) throw ApiErrors.Unavailable("The idempotency record is changing concurrently. Retry the request.");

                var existing = await db.IdempotencyRecords.AsNoTracking()
                    .FirstOrDefaultAsync(r => r.UserId == userId && r.Operation == operation && r.IdempotencyKey == key, token);
                if (existing is null) continue; // Araya giren silme: yeniden dene.

                if (existing.ExpiresAt <= now)
                {
                    // Süresi dolmuş kayıt yok sayılır; çift etkiyi iş düzeyi unique kısıtlar engeller.
                    await db.IdempotencyRecords
                        .Where(r => r.UserId == userId && r.Operation == operation && r.IdempotencyKey == key && r.ExpiresAt <= now)
                        .ExecuteDeleteAsync(token);
                    continue;
                }

                if (existing.HashVersion != CanonicalRequest.HashVersion || !string.Equals(existing.RequestHash, hash, StringComparison.Ordinal))
                    throw ApiErrors.Conflict(ErrorCodes.IdempotencyKeyReused, $"This {KeyHeader} was already used with a different request.");

                return new Stored(existing.ResponseStatus!.Value, existing.ResponseBody!, existing.ResponseContentType!, existing.ResponseLocation, Replayed: true);
            }

            var result = await handler(request, token);
            if (result.StatusCode is < 200 or > 299)
                throw new InvalidOperationException("Idempotent handlers return only success results; failures must be thrown.");

            var body = JsonSerializer.SerializeToUtf8Bytes(result.Body, result.Body.GetType(), JsonDefaults.Options);
            var stored = await db.IdempotencyRecords
                .Where(r => r.UserId == userId && r.Operation == operation && r.IdempotencyKey == key)
                .ExecuteUpdateAsync(u => u
                    .SetProperty(r => r.ResponseStatus, result.StatusCode)
                    .SetProperty(r => r.ResponseContentType, JsonContentType)
                    .SetProperty(r => r.ResponseLocation, result.Location)
                    .SetProperty(r => r.ResponseBody, body), token);
            if (stored != 1) throw new InvalidOperationException("Idempotency claim disappeared within its own transaction.");
            return new Stored(result.StatusCode, body, JsonContentType, result.Location, Replayed: false);
        }, ct);

        // Commit tamam; buradan sonra yanıt kaybolsa bile aynı anahtar aynı baytları getirir.
        await failpoints.HitAsync("idempotency.after-commit", ct);
        return new StoredResult(outcome);
    }

    private static TRequest ParseNoBody<TRequest>(byte[] raw) where TRequest : class
    {
        if (raw.Length != 0 && !(raw.Length == 2 && raw[0] == (byte)'{' && raw[1] == (byte)'}'))
            throw ApiErrors.BadRequest(ErrorCodes.ValidationFailed, "This operation does not accept a request body.");
        return (TRequest)(object)new NoBody();
    }

    private sealed record Stored(int Status, byte[] Body, string ContentType, string? Location, bool Replayed);

    private sealed class StoredResult(Stored stored) : IResult
    {
        public async Task ExecuteAsync(HttpContext http)
        {
            http.Response.StatusCode = stored.Status;
            http.Response.ContentType = stored.ContentType;
            http.Response.ContentLength = stored.Body.Length;
            http.Response.Headers.CacheControl = "no-store";
            if (stored.Location is not null) http.Response.Headers.Location = stored.Location;
            if (stored.Replayed) http.Response.Headers[ReplayHeader] = "true";
            await http.Response.Body.WriteAsync(stored.Body, http.RequestAborted);
        }
    }
}
