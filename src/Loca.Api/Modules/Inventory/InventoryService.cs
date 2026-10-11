using Loca.Api.Infrastructure.Http;
using Loca.Api.Infrastructure.Persistence;
using Loca.Domain;
using Microsoft.EntityFrameworkCore;

namespace Loca.Api.Modules.Inventory;

public sealed record GaSelection(Guid CategoryId, int Quantity);

/// <summary>Rezervasyon kapandığında diğer modüllerin (sipariş) aynı transaction içinde tepki vermesi için.</summary>
public interface IReservationLifecycleHook
{
    Task OnClosedWithoutSaleAsync(Reservation reservation, ReservationStatus closedAs, DateTimeOffset now, CancellationToken ct);
}

/// <summary>
/// Envanter tablolarını değiştiren tek yer (ADR 0002). Tüm metotlar çağıranın transaction'ı içinde çalışır;
/// kilitler her zaman kimliğe göre artan sırada alınır.
/// </summary>
public sealed class InventoryService(AppDbContext db, IEnumerable<IReservationLifecycleHook> hooks)
{
    // --- Kilitleme ------------------------------------------------------------------------------

    /// <summary>Kümenin kilit kökü. Çağıran bu kilidi aldıktan SONRA saati okumalıdır.</summary>
    public Task<Reservation?> LockReservationAsync(Guid reservationId, CancellationToken ct) =>
        db.Reservations.FromSql($"SELECT * FROM inventory.reservations WHERE id = {reservationId} FOR UPDATE").SingleOrDefaultAsync(ct);

    private Task<List<SessionSeat>> LockSeatsAsync(Guid sessionId, Guid[] seatIds, CancellationToken ct) =>
        db.SessionSeats.FromSql($"SELECT * FROM inventory.session_seats WHERE session_id = {sessionId} AND id = ANY({seatIds}) ORDER BY id FOR UPDATE").ToListAsync(ct);

    /// <summary>İstenen koltuklar ile (varsa) çağıranın süresi dolmuş kendi hold'unun koltukları tek sıralı geçişte kilitlenir.</summary>
    private Task<List<SessionSeat>> LockSeatsAsync(Guid sessionId, Guid[] seatIds, Guid alsoHeldBy, CancellationToken ct) =>
        db.SessionSeats.FromSql($"""
            SELECT * FROM inventory.session_seats
            WHERE session_id = {sessionId} AND (id = ANY({seatIds}) OR hold_reservation_id = {alsoHeldBy})
            ORDER BY id FOR UPDATE
            """).ToListAsync(ct);

    private Task<List<GaCategory>> LockCategoriesAsync(Guid[] categoryIds, CancellationToken ct) =>
        db.GaCategories.FromSql($"SELECT * FROM inventory.ga_categories WHERE id = ANY({categoryIds}) ORDER BY id FOR UPDATE").ToListAsync(ct);

    // --- Claim ----------------------------------------------------------------------------------

    /// <summary>
    /// Hep-ya-hiç koltuk claim'i. Bir koltuk boşsa veya hold'unun süresi dolmuşsa (<c>held_until &lt;= now</c>)
    /// alınabilir; süresi dolmuş hold'un koltuğu worker beklenmeden devralınır.
    /// </summary>
    /// <param name="ownStale">Çağıranın aynı seanstaki süresi dolmuş (kilitlenmiş) hold'u; claim'den önce kapatılır.</param>
    public async Task<List<ReservationLine>> ClaimSeatsAsync(Reservation reservation, IReadOnlyList<Guid> seatIds, DateTimeOffset now,
        Reservation? ownStale, CancellationToken ct)
    {
        var ids = seatIds.Order().ToArray();
        var locked = ownStale is null
            ? await LockSeatsAsync(reservation.SessionId, ids, ct)
            : await LockSeatsAsync(reservation.SessionId, ids, ownStale.Id, ct);
        if (ownStale is not null)
        {
            // Kilitler zaten tek sıralı geçişte alındı; kapatma yeni kilit beklemez.
            await CloseWithoutSaleAsync(ownStale, ReservationStatus.Expired, "Expired", now, ct);
            await db.SaveChangesAsync(ct); // Partial unique indeks: eski satır kapanmadan yenisi eklenemez.
        }
        var seats = locked.Where(s => ids.Contains(s.Id)).ToList();
        if (seats.Count != ids.Length)
            throw ApiErrors.Unprocessable(ErrorCodes.InvalidSelection, "One or more seats do not belong to this session.");

        var unavailable = seats.Where(s => !(s.Status == SeatStatus.Available || (s.Status == SeatStatus.Held && s.HeldUntil <= now))).ToList();
        if (unavailable.Count > 0)
            throw ApiErrors.Conflict(ErrorCodes.SeatsUnavailable, "One or more selected seats are no longer available. Nothing was reserved.",
                new Dictionary<string, object?> { ["seatIds"] = unavailable.Select(s => s.Id).ToArray() });

        var lines = new List<ReservationLine>(seats.Count);
        foreach (var seat in seats)
        {
            seat.Status = SeatStatus.Held;
            seat.HoldReservationId = reservation.Id;
            seat.HeldUntil = reservation.ExpiresAt;
            lines.Add(new ReservationLine
            {
                Id = Guid.CreateVersion7(), ReservationId = reservation.Id, LineNo = lines.Count + 1, Kind = ReservationLineKind.Seat,
                SessionSeatId = seat.Id, Quantity = 1, UnitPriceMinor = seat.PriceMinor, Label = seat.Label,
            });
        }
        return lines;
    }

    /// <summary>
    /// Hep-ya-hiç genel giriş claim'i. Seansın tüm kategorileri sıralı kilitlenir; kapasite yetmezse süresi
    /// dolmuş hold'lar <c>SKIP LOCKED</c> ile toplanıp kapatılır ve yeniden bakılır.
    /// </summary>
    public async Task<List<ReservationLine>> ClaimGeneralAdmissionAsync(Reservation reservation, IReadOnlyList<GaSelection> selections, DateTimeOffset now,
        Reservation? ownStale, CancellationToken ct)
    {
        var categories = await db.GaCategories
            .FromSql($"SELECT * FROM inventory.ga_categories WHERE session_id = {reservation.SessionId} ORDER BY id FOR UPDATE").ToListAsync(ct);
        if (ownStale is not null)
        {
            await CloseWithoutSaleAsync(ownStale, ReservationStatus.Expired, "Expired", now, ct);
            await db.SaveChangesAsync(ct);
        }
        var byId = categories.ToDictionary(c => c.Id);
        if (selections.Any(s => !byId.ContainsKey(s.CategoryId)))
            throw ApiErrors.Unprocessable(ErrorCodes.InvalidSelection, "One or more ticket categories do not belong to this session.");

        bool Short() => selections.Any(s => byId[s.CategoryId].Capacity - byId[s.CategoryId].HeldCount - byId[s.CategoryId].SoldCount < s.Quantity);

        if (Short())
        {
            // Başka bir işlem tarafından kilitli olan süresi dolmuş hold atlanır (beklenmez → deadlock yok);
            // o an toplanamadığı için sonuç en kötü ihtimalle temkinli "yetersiz kapasite" olur, asla oversell değil.
            var stale = await db.Reservations.FromSql($"""
                SELECT * FROM inventory.reservations
                WHERE session_id = {reservation.SessionId} AND status = 'Active' AND expires_at <= {now}
                ORDER BY id FOR UPDATE SKIP LOCKED
                """).ToListAsync(ct);
            foreach (var expired in stale)
                await CloseWithoutSaleAsync(expired, ReservationStatus.Expired, "Expired", now, ct);
        }

        if (Short())
            throw ApiErrors.Conflict(ErrorCodes.InsufficientCapacity, "Not enough tickets are available. Nothing was reserved.",
                new Dictionary<string, object?>
                {
                    ["categories"] = selections.Select(s => new
                    {
                        categoryId = s.CategoryId,
                        requested = s.Quantity,
                        available = Math.Max(0, byId[s.CategoryId].Capacity - byId[s.CategoryId].HeldCount - byId[s.CategoryId].SoldCount),
                    }).ToArray(),
                });

        var lines = new List<ReservationLine>(selections.Count);
        foreach (var selection in selections.OrderBy(s => byId[s.CategoryId].SortOrder).ThenBy(s => s.CategoryId))
        {
            var category = byId[selection.CategoryId];
            category.HeldCount += selection.Quantity;
            lines.Add(new ReservationLine
            {
                Id = Guid.CreateVersion7(), ReservationId = reservation.Id, LineNo = lines.Count + 1, Kind = ReservationLineKind.GeneralAdmission,
                GaCategoryId = category.Id, Quantity = selection.Quantity, UnitPriceMinor = category.PriceMinor, Label = category.Name,
            });
        }
        return lines;
    }

    // --- Kapanış --------------------------------------------------------------------------------

    /// <summary>
    /// Aktif rezervasyonu satış olmadan kapatır (Expired / Released) ve hâlâ ona ait olan envanteri serbest
    /// bırakır. Çağıran rezervasyon satırı kilidini tutuyor olmalıdır.
    /// </summary>
    public async Task CloseWithoutSaleAsync(Reservation reservation, ReservationStatus closedAs, string reason, DateTimeOffset now, CancellationToken ct)
    {
        StateMachines.Ensure(StateMachines.CanTransition(reservation.Status, closedAs) && closedAs != ReservationStatus.Consumed, reservation.Status, closedAs);
        var lines = await db.ReservationLines.AsNoTracking().Where(l => l.ReservationId == reservation.Id).OrderBy(l => l.LineNo).ToListAsync(ct);

        // Koltuk: yalnız hâlâ bu rezervasyona ait olanlar (devralınmış koltuklara dokunulmaz).
        if (lines.Any(l => l.Kind == ReservationLineKind.Seat))
        {
            var held = await db.SessionSeats.FromSql($"""
                SELECT * FROM inventory.session_seats
                WHERE hold_reservation_id = {reservation.Id} AND status = 'Held' ORDER BY id FOR UPDATE
                """).ToListAsync(ct);
            foreach (var seat in held)
            {
                seat.Status = SeatStatus.Available;
                seat.HoldReservationId = null;
                seat.HeldUntil = null;
            }
        }

        var gaLines = lines.Where(l => l.Kind == ReservationLineKind.GeneralAdmission).ToList();
        if (gaLines.Count > 0)
        {
            var categories = (await LockCategoriesAsync([.. gaLines.Select(l => l.GaCategoryId!.Value).Distinct().Order()], ct)).ToDictionary(c => c.Id);
            foreach (var line in gaLines)
                categories[line.GaCategoryId!.Value].HeldCount -= line.Quantity;
        }

        reservation.Status = closedAs;
        reservation.ClosedAt = now;
        reservation.CloseReason = reason;
        foreach (var hook in hooks)
            await hook.OnClosedWithoutSaleAsync(reservation, closedAs, now, ct);
    }

    /// <summary>
    /// Hold'u satışa çevirir. Koltuklardan herhangi biri artık bu rezervasyona ait değilse (süre dolup
    /// devralınmışsa) hiçbir şeyi değiştirmeden <c>false</c> döner. Çağıran rezervasyon kilidini tutar ve
    /// <c>now &lt; expires_at</c> koşulunu doğrulamıştır.
    /// </summary>
    public async Task<bool> TryConsumeAsync(Reservation reservation, DateTimeOffset now, CancellationToken ct)
    {
        if (reservation.Status != ReservationStatus.Active || !HoldPolicy.IsActive(now, reservation.ExpiresAt)) return false;
        var lines = await db.ReservationLines.AsNoTracking().Where(l => l.ReservationId == reservation.Id).ToListAsync(ct);

        var seatIds = lines.Where(l => l.Kind == ReservationLineKind.Seat).Select(l => l.SessionSeatId!.Value).Order().ToArray();
        if (seatIds.Length > 0)
        {
            var seats = await LockSeatsAsync(reservation.SessionId, seatIds, ct);
            if (seats.Count != seatIds.Length || seats.Any(s => s.Status != SeatStatus.Held || s.HoldReservationId != reservation.Id))
                return false;
            foreach (var seat in seats)
            {
                seat.Status = SeatStatus.Sold;
                seat.HeldUntil = null;
            }
        }

        var gaLines = lines.Where(l => l.Kind == ReservationLineKind.GeneralAdmission).ToList();
        if (gaLines.Count > 0)
        {
            var categories = (await LockCategoriesAsync([.. gaLines.Select(l => l.GaCategoryId!.Value).Distinct().Order()], ct)).ToDictionary(c => c.Id);
            foreach (var line in gaLines)
            {
                var category = categories[line.GaCategoryId!.Value];
                category.HeldCount -= line.Quantity;
                category.SoldCount += line.Quantity;
            }
        }

        reservation.Status = ReservationStatus.Consumed;
        reservation.ClosedAt = now;
        reservation.CloseReason = "Paid";
        return true;
    }

    /// <summary>
    /// Ödenmiş ve iptal edilen siparişin envanterini yeniden satışa açar (yalnız müşteri iptalinde; seans
    /// iptalinde çağrılmaz). Çağıran rezervasyon kilidini tutar.
    /// </summary>
    public async Task ReopenSoldAsync(Reservation reservation, CancellationToken ct)
    {
        if (reservation.Status != ReservationStatus.Consumed)
            throw new InvalidOperationException("Only consumed reservations hold sold inventory.");
        var lines = await db.ReservationLines.AsNoTracking().Where(l => l.ReservationId == reservation.Id).ToListAsync(ct);

        var seatIds = lines.Where(l => l.Kind == ReservationLineKind.Seat).Select(l => l.SessionSeatId!.Value).Order().ToArray();
        if (seatIds.Length > 0)
        {
            foreach (var seat in (await LockSeatsAsync(reservation.SessionId, seatIds, ct)).Where(s => s.Status == SeatStatus.Sold && s.HoldReservationId == reservation.Id))
            {
                seat.Status = SeatStatus.Available;
                seat.HoldReservationId = null;
                seat.HeldUntil = null;
            }
        }

        var gaLines = lines.Where(l => l.Kind == ReservationLineKind.GeneralAdmission).ToList();
        if (gaLines.Count > 0)
        {
            var categories = (await LockCategoriesAsync([.. gaLines.Select(l => l.GaCategoryId!.Value).Distinct().Order()], ct)).ToDictionary(c => c.Id);
            foreach (var line in gaLines)
                categories[line.GaCategoryId!.Value].SoldCount -= line.Quantity;
        }
    }
}
