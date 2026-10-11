using Loca.Api.Infrastructure.Idempotency;
using Loca.Api.Infrastructure.Persistence;
using Loca.Api.Infrastructure.Time;
using Loca.Api.Infrastructure.Tx;
using Loca.Api.Infrastructure.Workers;
using Loca.Api.Modules.Operations;
using Loca.Domain;
using Microsoft.EntityFrameworkCore;

namespace Loca.Api.Modules.Inventory;

/// <summary>
/// Süresi dolmuş hold'ları kapatır ve envanteri serbest bırakır. İş tümüyle tek transaction olduğundan
/// lease sütunu yerine <c>FOR UPDATE SKIP LOCKED</c> kullanılır: worker çökerse transaction geri alınır,
/// başka bir worker aynı satırları yeniden görür. Doğruluk bu worker'ın zamanında çalışmasına bağlı değildir.
/// </summary>
public sealed class HoldExpiryProcessor(
    AppDbContext db, IClock clock, ITransactionRunner transactions, InventoryService inventory, IAuditWriter audit, IFailpoints failpoints) : IBatchProcessor
{
    public string Name => "hold-expiry";

    public Task<int> RunOnceAsync(CancellationToken ct) => RunOnceAsync(batchSize: 50, ct);

    public Task<int> RunOnceAsync(int batchSize, CancellationToken ct) => transactions.RunAsync(async token =>
    {
        var now = await clock.NowAsync(token);
        var expired = await db.Reservations.FromSql($"""
            SELECT * FROM inventory.reservations
            WHERE status = 'Active' AND expires_at <= {now}
            ORDER BY expires_at, id LIMIT {batchSize} FOR UPDATE SKIP LOCKED
            """).ToListAsync(token);
        if (expired.Count > 0) await failpoints.HitAsync("hold-expiry.after-lock", token);

        foreach (var reservation in expired)
        {
            await inventory.CloseWithoutSaleAsync(reservation, ReservationStatus.Expired, "Expired", now, token);
            audit.Add(now, "hold.expired", "reservation", reservation.Id, actorKind: "Worker");
        }
        await db.SaveChangesAsync(token);
        return expired.Count;
    }, ct);
}
