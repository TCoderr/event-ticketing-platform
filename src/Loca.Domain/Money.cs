namespace Loca.Domain;

/// <summary>
/// Minor unit (kuruş) tam sayı para. Kayan nokta kullanılmaz; yalnız TRY desteklenir.
/// </summary>
public readonly record struct Money
{
    public const string SupportedCurrency = "TRY";

    /// <summary>Tek satır/sipariş için kabul edilen üst sınır (1.000.000,00 TRY); taşma ve yazım hatasına karşı.</summary>
    public const long MaxMinor = 100_000_000;

    public long Minor { get; }
    public string Currency => SupportedCurrency;

    private Money(long minor) => Minor = minor;

    public static Money Zero => new(0);

    public static Money FromMinor(long minor, string currency = SupportedCurrency)
    {
        if (!IsSupportedCurrency(currency))
            throw new DomainRuleException(ErrorCodes.UnsupportedCurrency, $"Unsupported currency '{currency}'.");
        if (minor < 0)
            throw new DomainRuleException(ErrorCodes.ValidationFailed, "Amount cannot be negative.");
        if (minor > MaxMinor)
            throw new DomainRuleException(ErrorCodes.ValidationFailed, "Amount exceeds the supported maximum.");
        return new Money(minor);
    }

    public static bool IsSupportedCurrency(string? currency) =>
        string.Equals(currency, SupportedCurrency, StringComparison.Ordinal);

    public static bool IsValidPriceMinor(long minor) => minor is >= 0 and <= MaxMinor;

    public Money Add(Money other) => new(checked(Minor + other.Minor));

    public Money Multiply(int quantity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(quantity);
        return new Money(checked(Minor * quantity));
    }

    public override string ToString() => $"{Minor / 100}.{Minor % 100:00} {Currency}";
}
