using Npgsql;

namespace Loca.IntegrationTests.Infrastructure;

/// <summary>Envanter durumunu doğrudan veritabanında kuran test düzeneği (yalnız tek kullanımlık test veritabanlarında).</summary>
public static class InventoryTestData
{
    /// <summary>Bir koltuğu, tüketilmiş (satılmış) bir rezervasyona ait olacak şekilde işaretler.</summary>
    public static Task MarkOneSeatSoldAsync(TestDatabase database, Guid sessionId, string? tier = null) =>
        database.ExecuteAsAdminAsync(
            """
            WITH seat AS (
                SELECT id, price_minor, section || ' / ' || row_label || ' / ' || seat_number AS label
                FROM inventory.session_seats
                WHERE session_id = @s AND status = 'Available' AND (@t::text IS NULL OR tier_code = @t) ORDER BY id LIMIT 1
            ), reservation AS (
                INSERT INTO inventory.reservations (id, session_id, user_id, status, created_at, expires_at, ticket_count, total_minor, currency, closed_at, close_reason)
                SELECT gen_random_uuid(), @s, (SELECT id FROM identity.users ORDER BY id LIMIT 1), 'Consumed', now(), now(), 1, seat.price_minor, 'TRY', now(), 'Paid'
                FROM seat RETURNING id
            ), line AS (
                INSERT INTO inventory.reservation_lines (id, reservation_id, line_no, kind, session_seat_id, quantity, unit_price_minor, label)
                SELECT gen_random_uuid(), reservation.id, 1, 'Seat', seat.id, 1, seat.price_minor, seat.label FROM reservation, seat
            )
            UPDATE inventory.session_seats SET status = 'Sold', hold_reservation_id = (SELECT id FROM reservation), held_until = NULL
            WHERE id = (SELECT id FROM seat)
            """,
            new NpgsqlParameter("s", sessionId), new NpgsqlParameter("t", (object?)tier ?? DBNull.Value) { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
}
