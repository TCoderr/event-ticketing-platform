using System.Globalization;
using System.Threading.RateLimiting;
using Loca.Api.Infrastructure.Auth;
using Loca.Api.Infrastructure.Http;
using Loca.Domain;
using Microsoft.AspNetCore.RateLimiting;

namespace Loca.Api.Infrastructure.RateLimiting;

/// <summary>Dakika başına izinler. Gerekçeler: docs/06-security-and-failure-behavior.md §4.</summary>
public sealed class RateLimitOptions
{
    public const string Section = "RateLimits";
    public int LoginPerMinute { get; set; } = 10;
    public int RegisterPerMinute { get; set; } = 5;
    public int CatalogPerMinute { get; set; } = 300;
    public int HoldPerMinute { get; set; } = 20;
    public int CheckoutPerMinute { get; set; } = 10;
    public int CheckInPerMinute { get; set; } = 120;
    public int WebhookPerMinute { get; set; } = 600;
}

public static class RateLimitPolicies
{
    public const string Login = "login";
    public const string Register = "register";
    public const string Catalog = "catalog";
    public const string Hold = "hold";
    public const string Checkout = "checkout";
    public const string CheckIn = "checkin";
    public const string Webhook = "webhook";

    public static void Configure(RateLimiterOptions limiter, RateLimitOptions limits)
    {
        limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        limiter.OnRejected = async (context, _) =>
        {
            var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var value) ? (int)Math.Ceiling(value.TotalSeconds) : 60;
            context.HttpContext.Response.Headers.RetryAfter = retryAfter.ToString(CultureInfo.InvariantCulture);
            await ProblemWriter.WriteAsync(context.HttpContext, StatusCodes.Status429TooManyRequests, ErrorCodes.RateLimited,
                "Too many requests. Retry after the indicated delay.",
                new Dictionary<string, object?> { ["retryAfterSeconds"] = retryAfter });
        };

        // Anonim uçlar: bağlantının gerçek uzak adresi. İletilen başlıklar varsayılan olarak işlenmez (ADR 0008).
        AddFixedWindow(limiter, Login, limits.LoginPerMinute, ByAddress);
        AddFixedWindow(limiter, Register, limits.RegisterPerMinute, ByAddress);
        AddFixedWindow(limiter, Catalog, limits.CatalogPerMinute, ByAddress);

        // Kimliği doğrulanmış uçlar: kullanıcı başına ayrı kova; müşteriler tek kovayı paylaşmaz.
        AddFixedWindow(limiter, Hold, limits.HoldPerMinute, ByUser);
        AddFixedWindow(limiter, Checkout, limits.CheckoutPerMinute, ByUser);
        AddFixedWindow(limiter, CheckIn, limits.CheckInPerMinute, ByUser);

        AddFixedWindow(limiter, Webhook, limits.WebhookPerMinute, _ => "webhook");
    }

    private static void AddFixedWindow(RateLimiterOptions limiter, string policy, int permitsPerMinute, Func<HttpContext, string> partition)
    {
        limiter.AddPolicy(policy, http => RateLimitPartition.GetFixedWindowLimiter(
            $"{policy}:{partition(http)}",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = Math.Max(1, permitsPerMinute),
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true,
            }));
    }

    private static string ByAddress(HttpContext http) => http.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    private static string ByUser(HttpContext http) =>
        http.Items.TryGetValue(HttpCurrentUser.ItemKey, out var value) && value is ValidatedSession session
            ? session.UserId.ToString("N")
            : "anon:" + ByAddress(http);
}
