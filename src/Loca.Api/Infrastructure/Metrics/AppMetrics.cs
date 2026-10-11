using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace Loca.Api.Infrastructure.Metrics;

/// <summary>
/// Uygulama sayaçları. Etiketler bilinçli olarak düşük kardinalitelidir: yalnız aşağıdaki anahtarlar ve
/// kısa, sabit kümeden gelen değerler kabul edilir. Sipariş/bilet/müşteri kimliği, e-posta veya token
/// HİÇBİR ZAMAN etiket olamaz; izin verilmeyen etiket kaydı reddedilir.
/// </summary>
public sealed class AppMetrics : IDisposable
{
    public const string MeterName = "Loca";

    public static readonly IReadOnlySet<string> AllowedLabelKeys = new HashSet<string>(StringComparer.Ordinal) { "mode", "outcome", "result", "type" };

    private const int MaxLabelValueLength = 48;
    private readonly Meter _meter = new(MeterName);
    private readonly ConcurrentDictionary<string, Counter<long>> _counters = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _values = new(StringComparer.Ordinal);

    public void Increment(string name, params (string Key, string Value)[] labels)
    {
        foreach (var (key, value) in labels)
        {
            if (!AllowedLabelKeys.Contains(key))
                throw new ArgumentException($"Metric label '{key}' is not allowed.", nameof(labels));
            if (!IsSafeValue(value))
                throw new ArgumentException($"Metric label value for '{key}' is not a low-cardinality code.", nameof(labels));
        }

        var counter = _counters.GetOrAdd(name, static (n, meter) => meter.CreateCounter<long>(n), _meter);
        counter.Add(1, [.. labels.Select(l => new KeyValuePair<string, object?>(l.Key, l.Value))]);

        var series = labels.Length == 0 ? name : $"{name}{{{string.Join(",", labels.OrderBy(l => l.Key, StringComparer.Ordinal).Select(l => $"{l.Key}=\"{l.Value}\""))}}}";
        _values.AddOrUpdate(series, 1, static (_, current) => current + 1);
    }

    /// <summary>Değer kısa bir kod olmalıdır: küçük/büyük harf, rakam, alt çizgi, nokta, iki nokta, tire.</summary>
    private static bool IsSafeValue(string value)
    {
        if (value.Length is 0 or > MaxLabelValueLength) return false;
        foreach (var c in value)
            if (!(char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or ':' or '-')) return false;
        // Kimlik benzeri değerler (UUID) kod değildir.
        return !Guid.TryParse(value, out _);
    }

    public IReadOnlyDictionary<string, long> Snapshot() => new SortedDictionary<string, long>(_values, StringComparer.Ordinal);

    public void Dispose() => _meter.Dispose();
}
