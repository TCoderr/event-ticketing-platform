using Loca.Api.Infrastructure.Persistence;
using Loca.Api.Infrastructure.Time;
using Loca.Api.Infrastructure.Workers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Loca.Api.Modules.Operations;

/// <summary>Saklama süreleri (demo varsayımı; hukuki uygunluk iddiası değildir). docs/04 §4.</summary>
public sealed class RetentionOptions
{
    public const string Section = "Retention";
    public int InboxDays { get; set; } = 30;
    public int ProcessedOutboxDays { get; set; } = 7;
    public int ClosedSessionDays { get; set; } = 30;
    public int IntervalMinutes { get; set; } = 15;
}

public sealed class RetentionSchedule
{
    private readonly Lock _gate = new();
    private DateTimeOffset _nextRun = DateTimeOffset.MinValue;

    public bool TryStart(DateTimeOffset now, TimeSpan interval)
    {
        lock (_gate)
        {
            if (now < _nextRun) return false;
            _nextRun = now.Add(interval);
            return true;
        }
    }
}

public sealed record RetentionReport(int IdempotencyRecords, int InboxMessages, int OutboxMessages, int UserSessions)
{
    public int Total => IdempotencyRecords + InboxMessages + OutboxMessages + UserSessions;
}

/// <summary>
/// Süresi dolmuş teknik kayıtları siler. Bu tabloların hiçbiri iş tablolarına FK vermez/almaz; bu yüzden
/// TTL ile silme sipariş, ödeme, iade, bilet veya denetim kaydını etkileyemez. Dead-letter mesajlar silinmez.
/// </summary>
public sealed class RetentionProcessor(AppDbContext db, IClock clock, IOptions<RetentionOptions> options, RetentionSchedule schedule) : IBatchProcessor
{
    public string Name => "retention";

    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        // İşlemci her turda yeniden oluşturulur; son çalışma zamanı süreç ömürlü zamanlayıcıda tutulur.
        var now = await clock.NowAsync(ct);
        if (!schedule.TryStart(now, TimeSpan.FromMinutes(options.Value.IntervalMinutes))) return 0;
        await PurgeAsync(ct);
        return 0; // Sıkı döngüye girmesin: bir sonraki tur aralık dolunca.
    }

    public async Task<RetentionReport> PurgeAsync(CancellationToken ct)
    {
        var now = await clock.NowAsync(ct);
        var opts = options.Value;
        var inboxBefore = now.AddDays(-opts.InboxDays);
        var outboxBefore = now.AddDays(-opts.ProcessedOutboxDays);
        var sessionsBefore = now.AddDays(-opts.ClosedSessionDays);

        var idempotency = await db.IdempotencyRecords.Where(r => r.ExpiresAt <= now).ExecuteDeleteAsync(ct);
        var inbox = await db.InboxMessages.Where(m => m.ReceivedAt <= inboxBefore).ExecuteDeleteAsync(ct);
        var outbox = await db.OutboxMessages.Where(m => m.ProcessedAt != null && m.ProcessedAt <= outboxBefore && m.DeadLetteredAt == null).ExecuteDeleteAsync(ct);
        var sessions = await db.UserSessions
            .Where(s => (s.RevokedAt != null && s.RevokedAt <= sessionsBefore) || s.ExpiresAt <= sessionsBefore).ExecuteDeleteAsync(ct);
        return new RetentionReport(idempotency, inbox, outbox, sessions);
    }
}
