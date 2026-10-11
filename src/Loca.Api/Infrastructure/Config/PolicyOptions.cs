using Loca.Domain;

namespace Loca.Api.Infrastructure.Config;

/// <summary>Hold süresi sınırları. Seans kendi süresini bu aralıkta seçer.</summary>
public sealed class HoldOptions
{
    public const string Section = "Holds";
    public int DefaultTtlSeconds { get; set; } = HoldPolicy.DefaultTtlSeconds;
    public int MinTtlSeconds { get; set; } = 120;
    public int MaxTtlSeconds { get; set; } = 900;
}

/// <summary>Demo için varsayılan zaman kuralları (docs/01 §4). Hukuki/sektörel standart değildir.</summary>
public sealed class PolicyOptions
{
    public const string Section = "Policies";
    public int CustomerCancelCutoffHours { get; set; } = 24;
    public int CheckInOpensBeforeMinutes { get; set; } = 180;
    public int CheckInClosesAfterMinutes { get; set; } = 360;

    public TimeSpan CustomerCancelCutoff => TimeSpan.FromHours(CustomerCancelCutoffHours);
    public TimeSpan CheckInOpensBefore => TimeSpan.FromMinutes(CheckInOpensBeforeMinutes);
    public TimeSpan CheckInClosesAfter => TimeSpan.FromMinutes(CheckInClosesAfterMinutes);
}
