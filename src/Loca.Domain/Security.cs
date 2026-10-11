using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Loca.Domain;

/// <summary>Bilet taşıyıcı token'ı: 32 bayt CSPRNG, <c>LT1.</c> önekli base64url. Kişisel veri içermez.</summary>
public static class TicketToken
{
    public const string Prefix = "LT1.";
    private const int RandomBytes = 32;
    private const int EncodedLength = 43; // 32 bayt, dolgusuz base64url

    public static string Generate()
    {
        Span<byte> bytes = stackalloc byte[RandomBytes];
        RandomNumberGenerator.Fill(bytes);
        return Prefix + Base64Url(bytes);
    }

    public static bool IsWellFormed(string? token)
    {
        if (token is null || token.Length != Prefix.Length + EncodedLength) return false;
        if (!token.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        foreach (var c in token.AsSpan(Prefix.Length))
        {
            var ok = c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_';
            if (!ok) return false;
        }
        return true;
    }

    /// <summary>Veritabanında arama için özet. 256 bit entropili girdide anahtarsız SHA-256 yeterlidir.</summary>
    public static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    private static string Base64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>Deterministik bilet kimliği: aynı (sipariş, satır, sıra) her zaman aynı UUID'yi üretir.</summary>
public static class TicketIdentity
{
    private static readonly Guid Namespace = new("6f1d0c1e-7a57-5b0e-9d43-4c0c41a0e001");

    public static Guid For(Guid orderId, Guid reservationLineId, int ordinal) =>
        UuidV5(Namespace, $"{orderId:N}:{reservationLineId:N}:{ordinal.ToString(CultureInfo.InvariantCulture)}");

    internal static Guid UuidV5(Guid ns, string name)
    {
        Span<byte> nsBytes = stackalloc byte[16];
        ns.TryWriteBytes(nsBytes, bigEndian: true, out _);
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var input = new byte[16 + nameBytes.Length];
        nsBytes.CopyTo(input);
        nameBytes.CopyTo(input, 16);

        Span<byte> hash = stackalloc byte[20];
        SHA1.HashData(input, hash); // RFC 9562 UUIDv5 tanımı gereği SHA-1; güvenlik amacıyla kullanılmaz.
        var result = hash[..16];
        result[6] = (byte)((result[6] & 0x0F) | 0x50);
        result[8] = (byte)((result[8] & 0x3F) | 0x80);
        return new Guid(result, bigEndian: true);
    }
}

public enum WebhookVerification { Valid, Malformed, SignatureInvalid, TimestampOutOfRange }

/// <summary>
/// Webhook imzası: <c>X-Psp-Signature: t=&lt;unix&gt;,v1=&lt;hex&gt;</c>,
/// <c>v1 = HMAC-SHA256(secret, "&lt;t&gt;." + ham gövde baytları)</c>. İmza her zaman ham baytlar üzerinden hesaplanır.
/// </summary>
public static class WebhookSignature
{
    public static readonly TimeSpan DefaultTolerance = TimeSpan.FromMinutes(5);

    public static string Sign(ReadOnlySpan<byte> secret, long unixSeconds, ReadOnlySpan<byte> rawBody) =>
        $"t={unixSeconds.ToString(CultureInfo.InvariantCulture)},v1={ComputeHex(secret, unixSeconds, rawBody)}";

    public static WebhookVerification Verify(
        string? header, ReadOnlySpan<byte> rawBody, IReadOnlyCollection<byte[]> activeSecrets,
        DateTimeOffset now, TimeSpan tolerance)
    {
        if (string.IsNullOrEmpty(header) || header.Length > 512) return WebhookVerification.Malformed;

        long? timestamp = null;
        var candidates = new List<byte[]>();
        foreach (var part in header.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) return WebhookVerification.Malformed;
            var name = part[..eq];
            var value = part[(eq + 1)..];
            if (name == "t")
            {
                if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var t)) return WebhookVerification.Malformed;
                timestamp = t;
            }
            else if (name == "v1")
            {
                if (value.Length != 64) return WebhookVerification.Malformed;
                try { candidates.Add(Convert.FromHexString(value)); }
                catch (FormatException) { return WebhookVerification.Malformed; }
            }
        }

        if (timestamp is null || candidates.Count == 0) return WebhookVerification.Malformed;

        // Önce imza: imzasız bir istek zaman bilgisini sızdıran farklı bir yanıt alamasın.
        var matched = false;
        foreach (var secret in activeSecrets)
        {
            var expected = ComputeMac(secret, timestamp.Value, rawBody);
            foreach (var candidate in candidates)
                matched |= CryptographicOperations.FixedTimeEquals(expected, candidate);
        }
        if (!matched) return WebhookVerification.SignatureInvalid;

        var sentAt = DateTimeOffset.FromUnixTimeSeconds(timestamp.Value);
        return (now - sentAt).Duration() <= tolerance ? WebhookVerification.Valid : WebhookVerification.TimestampOutOfRange;
    }

    private static string ComputeHex(ReadOnlySpan<byte> secret, long unixSeconds, ReadOnlySpan<byte> rawBody) =>
        Convert.ToHexStringLower(ComputeMac(secret, unixSeconds, rawBody));

    private static byte[] ComputeMac(ReadOnlySpan<byte> secret, long unixSeconds, ReadOnlySpan<byte> rawBody)
    {
        var prefix = Encoding.ASCII.GetBytes(unixSeconds.ToString(CultureInfo.InvariantCulture) + ".");
        var payload = new byte[prefix.Length + rawBody.Length];
        prefix.CopyTo(payload, 0);
        rawBody.CopyTo(payload.AsSpan(prefix.Length));
        return HMACSHA256.HashData(secret, payload);
    }
}
