namespace Loca.Domain;

/// <summary>Zamana bağlı iş kuralları. Tüm <c>now</c> değerleri çağıran tarafından (veritabanı saati) verilir.</summary>
public static class HoldPolicy
{
    public const int MaxTicketsPerHold = 6;
    public const int DefaultTtlSeconds = 600;

    /// <summary>Hold yalnız <c>now &lt; expiresAt</c> iken aktiftir; eşitlik anında süresi dolmuştur.</summary>
    public static bool IsActive(DateTimeOffset now, DateTimeOffset expiresAt) => now < expiresAt;

    public static bool IsValidTicketCount(int count) => count is >= 1 and <= MaxTicketsPerHold;

    public static DateTimeOffset ExpiresAt(DateTimeOffset now, int ttlSeconds) =>
        Timestamps.TruncateToMicroseconds(now.AddSeconds(ttlSeconds));
}

public enum SalesWindowState { NotStarted, Open, Closed }

public static class SalesWindow
{
    public static SalesWindowState Evaluate(DateTimeOffset now, DateTimeOffset salesStartAt, DateTimeOffset salesEndAt)
    {
        if (now < salesStartAt) return SalesWindowState.NotStarted;
        return now < salesEndAt ? SalesWindowState.Open : SalesWindowState.Closed;
    }

    public static bool IsValidDefinition(DateTimeOffset salesStartAt, DateTimeOffset salesEndAt, DateTimeOffset startsAt) =>
        salesStartAt < salesEndAt && salesEndAt <= startsAt;
}

public static class CancellationPolicy
{
    public static readonly TimeSpan DefaultCutoff = TimeSpan.FromHours(24);

    /// <summary>Müşteri iptali yalnız <c>now &lt; startsAt - cutoff</c> iken mümkündür.</summary>
    public static bool CustomerCanCancel(DateTimeOffset now, DateTimeOffset sessionStartsAt, TimeSpan cutoff) =>
        now < sessionStartsAt - cutoff;

    public static DateTimeOffset CancelDeadline(DateTimeOffset sessionStartsAt, TimeSpan cutoff) => sessionStartsAt - cutoff;
}

public static class CheckInWindow
{
    public static readonly TimeSpan DefaultOpensBefore = TimeSpan.FromHours(3);
    public static readonly TimeSpan DefaultClosesAfter = TimeSpan.FromHours(6);

    public static DateTimeOffset OpensAt(DateTimeOffset startsAt, TimeSpan opensBefore) => startsAt - opensBefore;
    public static DateTimeOffset ClosesAt(DateTimeOffset startsAt, TimeSpan closesAfter) => startsAt + closesAfter;

    /// <summary>Pencere yarı açıktır: <c>opensAt &lt;= now &lt; closesAt</c>.</summary>
    public static bool IsOpen(DateTimeOffset now, DateTimeOffset startsAt, TimeSpan opensBefore, TimeSpan closesAfter) =>
        now >= OpensAt(startsAt, opensBefore) && now < ClosesAt(startsAt, closesAfter);
}

public static class Timestamps
{
    private const long TicksPerMicrosecond = 10;

    /// <summary>PostgreSQL <c>timestamptz</c> mikrosaniye çözünürlüğündedir; .NET 100 ns. Karşılaştırmalar aynı çözünürlükte yapılır.</summary>
    public static DateTimeOffset TruncateToMicroseconds(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - utc.Ticks % TicksPerMicrosecond, TimeSpan.Zero);
    }
}

public sealed record LocalTimeResolution(bool Success, DateTimeOffset Utc, string? ErrorCode);

public static class LocalTimeResolver
{
    public static bool TryFindTimeZone(string? id, out TimeZoneInfo zone)
    {
        zone = TimeZoneInfo.Utc;
        if (string.IsNullOrWhiteSpace(id) || id.Length > 64) return false;
        // Yalnız IANA kimlikleri (Bölge/Şehir veya "UTC") kabul edilir; Windows kimlikleri reddedilir.
        if (id != "UTC" && !id.Contains('/')) return false;
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(id);
            return true;
        }
        catch (TimeZoneNotFoundException) { return false; }
        catch (InvalidTimeZoneException) { return false; }
    }

    /// <summary>
    /// Mekan yerel saatini UTC anına çevirir. DST boşluğundaki (var olmayan) ve DST çakışmasındaki
    /// (iki kez yaşanan) yerel saatler sessizce düzeltilmez, reddedilir.
    /// </summary>
    public static LocalTimeResolution Resolve(DateTime local, string timeZoneId)
    {
        if (!TryFindTimeZone(timeZoneId, out var zone))
            return new(false, default, ErrorCodes.InvalidTimezone);

        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(unspecified) || zone.IsAmbiguousTime(unspecified))
            return new(false, default, ErrorCodes.InvalidLocalTime);

        var offset = zone.GetUtcOffset(unspecified);
        return new(true, new DateTimeOffset(unspecified, offset).ToUniversalTime(), null);
    }

    public static DateTime ToLocal(DateTimeOffset utc, string timeZoneId) =>
        TryFindTimeZone(timeZoneId, out var zone)
            ? TimeZoneInfo.ConvertTime(utc, zone).DateTime
            : utc.UtcDateTime;
}
