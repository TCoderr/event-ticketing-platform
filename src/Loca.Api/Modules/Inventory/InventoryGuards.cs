using Loca.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Loca.Api.Modules.Inventory;

/// <summary>Katalog modülünün envanter durumuna dair sorabildiği dar sorular. Envanter tablolarına yalnız bu modül dokunur.</summary>
public interface IInventoryGuards
{
    /// <summary>Seans için (durumu ne olursa olsun) herhangi bir rezervasyon/satış izi var mı?</summary>
    Task<bool> SessionHasReservationsAsync(Guid sessionId, CancellationToken ct);
}

internal sealed class InventoryGuards(AppDbContext db) : IInventoryGuards
{
    public Task<bool> SessionHasReservationsAsync(Guid sessionId, CancellationToken ct) =>
        db.Reservations.AnyAsync(r => r.SessionId == sessionId, ct);
}
