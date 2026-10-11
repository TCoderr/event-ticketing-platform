using Loca.Api.Infrastructure.Persistence;
using Loca.Domain;
using Microsoft.EntityFrameworkCore;

namespace Loca.Api.Infrastructure.Time;

/// <summary>
/// Zamana bağlı tüm iş kararlarının saati (ADR 0005). Üretimde veritabanı saatidir; kilit alındıktan
/// sonra okunmalıdır ki kilit beklerken geçen süre karara yansısın.
/// </summary>
public interface IClock
{
    Task<DateTimeOffset> NowAsync(CancellationToken ct = default);
}

internal sealed class DbClock(AppDbContext db) : IClock
{
    public async Task<DateTimeOffset> NowAsync(CancellationToken ct = default)
    {
        // Geçerli bağlantı ve (varsa) transaction üzerinde çalışır. clock_timestamp() transaction
        // başlangıcını değil gerçek anı verir.
        var now = await db.Database
            .SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"")
            .SingleAsync(ct);
        return Timestamps.TruncateToMicroseconds(new DateTimeOffset(DateTime.SpecifyKind(now, DateTimeKind.Utc)));
    }
}
