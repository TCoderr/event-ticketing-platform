using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Loca.Domain;

/// <summary>
/// Idempotency istek özeti. Sürüm 1:
/// <c>SHA-256("v1\n" + METHOD + "\n" + yol + "\n" + kanonik JSON)</c>.
/// Kanonik JSON: nesne anahtarları ordinal sıralı, boşluksuz, dizi sırası korunur, metinler NFC.
/// </summary>
public static class CanonicalRequest
{
    public const int HashVersion = 1;
    public const int MinKeyLength = 16;
    public const int MaxKeyLength = 128;

    public static bool IsValidKey(string? key)
    {
        if (key is null || key.Length < MinKeyLength || key.Length > MaxKeyLength) return false;
        foreach (var c in key)
        {
            var ok = c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '.' or ':' or '-';
            if (!ok) return false;
        }
        return true;
    }

    /// <exception cref="JsonException">Gövde geçerli JSON değilse.</exception>
    public static byte[] CanonicalizeJson(ReadOnlySpan<byte> utf8Json)
    {
        if (utf8Json.IsEmpty) return [];
        var reader = new Utf8JsonReader(utf8Json, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false });
        using var doc = JsonDocument.ParseValue(ref reader);
        if (reader.Read())
            throw new JsonException("Unexpected trailing content after JSON value.");

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false, SkipValidation = false }))
        {
            Write(writer, doc.RootElement);
        }
        return buffer.WrittenSpan.ToArray();
    }

    public static string ComputeHash(string method, string path, ReadOnlySpan<byte> utf8JsonBody)
    {
        var canonicalBody = CanonicalizeJson(utf8JsonBody);
        var prefix = Encoding.UTF8.GetBytes($"v{HashVersion}\n{method.ToUpperInvariant()}\n{path}\n");
        var payload = new byte[prefix.Length + canonicalBody.Length];
        prefix.CopyTo(payload, 0);
        canonicalBody.CopyTo(payload, prefix.Length);
        return Convert.ToHexStringLower(SHA256.HashData(payload));
    }

    private static void Write(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject().OrderBy(p => p.Name.Normalize(NormalizationForm.FormC), StringComparer.Ordinal))
                {
                    var name = property.Name.Normalize(NormalizationForm.FormC);
                    if (!seen.Add(name))
                        throw new JsonException($"Duplicate property '{name}'.");
                    writer.WritePropertyName(name);
                    Write(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray()) Write(writer, item);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString()!.Normalize(NormalizationForm.FormC));
                break;
            case JsonValueKind.Number:
                // Sözleşmelerde yalnız tam sayı vardır; 1 ile 1.0 aynı özete gitsin diye tam sayılar normalize edilir.
                if (element.TryGetInt64(out var integer)) writer.WriteNumberValue(integer);
                else if (element.TryGetDecimal(out var dec))
                {
                    if (dec == decimal.Truncate(dec) && dec is >= long.MinValue and <= long.MaxValue) writer.WriteNumberValue((long)dec);
                    else writer.WriteNumberValue(dec);
                }
                else writer.WriteRawValue(element.GetRawText());
                break;
            case JsonValueKind.True: writer.WriteBooleanValue(true); break;
            case JsonValueKind.False: writer.WriteBooleanValue(false); break;
            default: writer.WriteNullValue(); break;
        }
    }
}
