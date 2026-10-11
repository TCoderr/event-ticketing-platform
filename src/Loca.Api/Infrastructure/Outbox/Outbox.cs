using System.Text.Json;
using Loca.Api.Infrastructure.Http;
using Loca.Api.Infrastructure.Metrics;
using Loca.Api.Infrastructure.Persistence;
using Loca.Api.Infrastructure.Time;
using Loca.Api.Infrastructure.Workers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Loca.Api.Infrastructure.Outbox;

/// <summary>
/// İş değişikliğiyle aynı transaction'da yazılan yan etki niyeti (ADR 0006). Teslim en az bir kezdir;
/// handler'lar idempotent olmalıdır. Sıra garantisi verilmez.
/// </summary>
public sealed class OutboxMessage
{
    public Guid Id { get; set; }
    public long Sequence { get; set; }
    public string Type { get; set; } = "";
    public string Payload { get; set; } = "{}";
    public string? CorrelationId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset AvailableAt { get; set; }
    public int AttemptCount { get; set; }
    public string? LeaseOwner { get; set; }

    /// <summary>Fencing token: her kiralamada yenilenir; tamamlama/erteleme yalnız token eşleşirse etkili olur.</summary>
    public Guid? LeaseToken { get; set; }

    public DateTimeOffset? LeaseUntil { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }
    public DateTimeOffset? DeadLetteredAt { get; set; }
    public string? LastErrorCode { get; set; }

    public T Read<T>() => JsonSerializer.Deserialize<T>(Payload, JsonDefaults.Options)
        ?? throw new OutboxPayloadException("Outbox payload is empty.");
}

/// <summary>Webhook dedupe kaydı. İş etkisiyle aynı transaction'da yazılır.</summary>
public sealed class InboxMessage
{
    public string Source { get; set; } = "";
    public string EventId { get; set; } = "";
    public string PayloadHash { get; set; } = "";
    public string Type { get; set; } = "";
    public DateTimeOffset ReceivedAt { get; set; }
    public string Outcome { get; set; } = "";
}

public sealed class OutboxPayloadException(string message) : Exception(message);

internal static class OutboxModel
{
    public static void Configure(ModelBuilder b)
    {
        b.Entity<OutboxMessage>(e =>
        {
            e.ToTable("outbox_messages", "ops");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Sequence).UseIdentityAlwaysColumn();
            e.Property(x => x.Sequence).Metadata.SetAfterSaveBehavior(Microsoft.EntityFrameworkCore.Metadata.PropertySaveBehavior.Ignore);
            e.Property(x => x.Type).HasMaxLength(60);
            e.Property(x => x.Payload).HasColumnType("jsonb");
            e.Property(x => x.CorrelationId).HasMaxLength(64);
            e.Property(x => x.LeaseOwner).HasMaxLength(80);
            e.Property(x => x.LastErrorCode).HasMaxLength(80);
            e.HasIndex(x => x.Sequence).IsUnique();
            e.HasIndex(x => new { x.AvailableAt, x.Sequence }).HasFilter("processed_at IS NULL AND dead_lettered_at IS NULL").HasDatabaseName("ix_outbox_messages_pending");
            e.HasIndex(x => x.DeadLetteredAt).HasFilter("dead_lettered_at IS NOT NULL");
        });

        b.Entity<InboxMessage>(e =>
        {
            e.ToTable("inbox_messages", "ops");
            e.HasKey(x => new { x.Source, x.EventId });
            e.Property(x => x.Source).HasMaxLength(20);
            e.Property(x => x.EventId).HasMaxLength(80);
            e.Property(x => x.PayloadHash).HasMaxLength(64).IsFixedLength();
            e.Property(x => x.Type).HasMaxLength(60);
            e.Property(x => x.Outcome).HasMaxLength(40);
            e.HasIndex(x => x.ReceivedAt);
        });
    }
}

public sealed class OutboxOptions
{
    public const string Section = "Outbox";
    public int MaxAttempts { get; set; } = 6;
    public int LeaseSeconds { get; set; } = 30;
    public int BaseDelayMs { get; set; } = 2000;
    public int MaxDelayMs { get; set; } = 60000;
    public int BatchSize { get; set; } = 20;
}

public interface IOutbox
{
    /// <summary>Mesajı geçerli DbContext'e ekler; çağıranın transaction'ı ile birlikte commit olur.</summary>
    void Enqueue(DateTimeOffset now, string type, object payload, DateTimeOffset? availableAt = null);
}

internal sealed class OutboxWriter(AppDbContext db, ICorrelationContext correlation) : IOutbox
{
    public void Enqueue(DateTimeOffset now, string type, object payload, DateTimeOffset? availableAt = null) =>
        db.OutboxMessages.Add(new OutboxMessage
        {
            Id = Guid.CreateVersion7(),
            Type = type,
            Payload = JsonSerializer.Serialize(payload, payload.GetType(), JsonDefaults.Options),
            CorrelationId = correlation.CorrelationId,
            CreatedAt = now,
            AvailableAt = availableAt ?? now,
        });
}

public interface IOutboxHandler
{
    string Type { get; }

    /// <summary>Tekrar çağrılabilir. Geçici hata için istisna fırlatır; dispatcher geri çekilerek yeniden dener.</summary>
    Task HandleAsync(OutboxMessage message, CancellationToken ct);

    /// <summary>Deneme bütçesi tükendiğinde, mesaj dead-letter'a alındıktan sonra bir kez çağrılır.</summary>
    Task OnDeadLetteredAsync(OutboxMessage message, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>
/// Outbox dispatcher. Mesajı kiralar (lease + fencing token), handler'ı transaction dışında çalıştırır,
/// sonucu yalnız token hâlâ kendisine aitse yazar. Süresi dolan lease başka bir tur tarafından devralınır.
/// </summary>
public sealed class OutboxProcessor(
    AppDbContext db, IClock clock, IServiceScopeFactory scopes, IOptions<OutboxOptions> options, AppMetrics metrics, ILogger<OutboxProcessor> logger) : IBatchProcessor
{
    private readonly string _owner = $"{Environment.MachineName}:{Guid.NewGuid():N}";

    public string Name => "outbox";

    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var claimed = await ClaimAsync(options.Value.BatchSize, ct);
        foreach (var message in claimed)
            await DispatchAsync(message, ct);
        return claimed.Count;
    }

    /// <summary>Kiralama tek ifadedir: seçim (SKIP LOCKED) ve işaretleme atomiktir.</summary>
    public async Task<List<OutboxMessage>> ClaimAsync(int batchSize, CancellationToken ct, string? ownerOverride = null)
    {
        var now = await clock.NowAsync(ct);
        var leaseUntil = now.AddSeconds(options.Value.LeaseSeconds);
        var owner = ownerOverride ?? _owner;
        return await db.OutboxMessages.FromSql($"""
            UPDATE ops.outbox_messages AS m
            SET lease_owner = {owner}, lease_token = gen_random_uuid(), lease_until = {leaseUntil}, attempt_count = m.attempt_count + 1
            WHERE m.id IN (
                SELECT id FROM ops.outbox_messages
                WHERE processed_at IS NULL AND dead_lettered_at IS NULL AND available_at <= {now}
                  AND (lease_until IS NULL OR lease_until <= {now})
                ORDER BY sequence LIMIT {batchSize} FOR UPDATE SKIP LOCKED)
            RETURNING m.*
            """).AsNoTracking().ToListAsync(ct);
    }

    public async Task DispatchAsync(OutboxMessage message, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetServices<IOutboxHandler>().FirstOrDefault(h => h.Type == message.Type);
        string errorCode;
        try
        {
            if (handler is null) throw new OutboxPayloadException($"No handler is registered for outbox type '{message.Type}'.");
            await handler.HandleAsync(message, ct);
            await CompleteAsync(message, ct);
            metrics.Increment("loca_outbox_total", ("outcome", "processed"));
            return;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return; // Kapanış: lease süresi dolunca mesaj yeniden alınır.
        }
        catch (Exception e)
        {
            errorCode = e is IHasErrorCode coded ? coded.ErrorCode : e.GetType().Name;
            logger.LogWarning("Outbox message {MessageId} of type {Type} failed on attempt {Attempt} with {ErrorCode}.",
                message.Id, message.Type, message.AttemptCount, errorCode);
        }

        if (message.AttemptCount >= options.Value.MaxAttempts)
        {
            metrics.Increment("loca_outbox_total", ("outcome", "dead_lettered"));
            if (await DeadLetterAsync(message, errorCode, ct) && handler is not null)
            {
                try { await handler.OnDeadLetteredAsync(message, ct); }
                catch (Exception e)
                {
                    logger.LogError("Dead-letter hook for outbox message {MessageId} failed with {ExceptionType}.", message.Id, e.GetType().Name);
                }
            }
        }
        else
        {
            metrics.Increment("loca_outbox_total", ("outcome", "retried"));
            await RescheduleAsync(message, errorCode, ct);
        }
    }

    /// <returns>Token eşleşmediyse (lease kaybedildiyse) <c>false</c>; yeni sahibin sonucu ezilmez.</returns>
    public async Task<bool> CompleteAsync(OutboxMessage message, CancellationToken ct)
    {
        var now = await clock.NowAsync(ct);
        var changed = await db.OutboxMessages
            .Where(m => m.Id == message.Id && m.LeaseToken == message.LeaseToken && m.ProcessedAt == null && m.DeadLetteredAt == null)
            .ExecuteUpdateAsync(u => u
                .SetProperty(m => m.ProcessedAt, now)
                .SetProperty(m => m.LeaseOwner, (string?)null)
                .SetProperty(m => m.LeaseUntil, (DateTimeOffset?)null)
                .SetProperty(m => m.LastErrorCode, (string?)null), ct);
        if (changed == 0)
            logger.LogWarning("Outbox message {MessageId} was completed after its lease was lost; the result was not recorded by this owner.", message.Id);
        return changed == 1;
    }

    public async Task<bool> RescheduleAsync(OutboxMessage message, string errorCode, CancellationToken ct)
    {
        var now = await clock.NowAsync(ct);
        var availableAt = now.Add(Backoff(message.AttemptCount));
        var changed = await db.OutboxMessages
            .Where(m => m.Id == message.Id && m.LeaseToken == message.LeaseToken && m.ProcessedAt == null && m.DeadLetteredAt == null)
            .ExecuteUpdateAsync(u => u
                .SetProperty(m => m.AvailableAt, availableAt)
                .SetProperty(m => m.LeaseOwner, (string?)null)
                .SetProperty(m => m.LeaseUntil, (DateTimeOffset?)null)
                .SetProperty(m => m.LastErrorCode, Truncate(errorCode)), ct);
        return changed == 1;
    }

    public async Task<bool> DeadLetterAsync(OutboxMessage message, string errorCode, CancellationToken ct)
    {
        var now = await clock.NowAsync(ct);
        var changed = await db.OutboxMessages
            .Where(m => m.Id == message.Id && m.LeaseToken == message.LeaseToken && m.ProcessedAt == null && m.DeadLetteredAt == null)
            .ExecuteUpdateAsync(u => u
                .SetProperty(m => m.DeadLetteredAt, now)
                .SetProperty(m => m.LeaseOwner, (string?)null)
                .SetProperty(m => m.LeaseUntil, (DateTimeOffset?)null)
                .SetProperty(m => m.LastErrorCode, Truncate(errorCode)), ct);
        if (changed == 1)
            logger.LogError("Outbox message {MessageId} of type {Type} was dead-lettered after {Attempts} attempts.", message.Id, message.Type, message.AttemptCount);
        return changed == 1;
    }

    /// <summary>Üstel geri çekilme + %20 jitter, üst sınırlı.</summary>
    public TimeSpan Backoff(int attempt)
    {
        var opts = options.Value;
        var exponential = opts.BaseDelayMs * Math.Pow(2, Math.Clamp(attempt - 1, 0, 16));
        var capped = Math.Min(exponential, opts.MaxDelayMs);
        var jitter = 1 + (Random.Shared.NextDouble() * 0.4 - 0.2);
        return TimeSpan.FromMilliseconds(Math.Max(1, capped * jitter));
    }

    private static string Truncate(string value) => value.Length <= 80 ? value : value[..80];
}

/// <summary>İstisnanın log/kayıt için güvenli (veri içermeyen) kodu.</summary>
public interface IHasErrorCode
{
    string ErrorCode { get; }
}
