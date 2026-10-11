using System.Diagnostics;
using System.Text.Json;
using Loca.Domain;

namespace Loca.Api.Infrastructure.Http;

public interface ICorrelationContext
{
    string? CorrelationId { get; }
}

internal sealed class HttpCorrelationContext(IHttpContextAccessor accessor) : ICorrelationContext
{
    public string? CorrelationId => accessor.HttpContext is { } http
        ? CorrelationIdMiddleware.Get(http)
        : Activity.Current?.GetBaggageItem(CorrelationIdMiddleware.BaggageKey);
}

/// <summary>Her isteğe korelasyon kimliği atar, yanıt başlığına ve log kapsamına ekler.</summary>
internal sealed class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    public const string HeaderName = "X-Correlation-Id";
    public const string BaggageKey = "loca.correlation_id";
    private const string ItemKey = "loca.correlation_id";

    public static string Get(HttpContext http) =>
        http.Items.TryGetValue(ItemKey, out var value) && value is string s ? s : http.TraceIdentifier;

    public async Task InvokeAsync(HttpContext http)
    {
        var supplied = http.Request.Headers[HeaderName].ToString();
        var id = IsAcceptable(supplied) ? supplied : Guid.CreateVersion7().ToString("N");
        http.Items[ItemKey] = id;
        http.Response.Headers[HeaderName] = id;
        using (logger.BeginScope(new Dictionary<string, object> { ["correlationId"] = id }))
        {
            await next(http);
        }
    }

    // İstemciden gelen değer log'a yazılacağı için biçimi sınırlandırılır.
    private static bool IsAcceptable(string value)
    {
        if (value.Length is < 8 or > 64) return false;
        foreach (var c in value)
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) return false;
        return true;
    }
}

/// <summary>
/// Kısa erişim logu. Yol yerine rota şablonu yazılır (düşük kardinalite; sorgu dizesi ve gövde asla loglanmaz).
/// </summary>
internal sealed class AccessLogMiddleware(RequestDelegate next, ILogger<AccessLogMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext http)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            await next(http);
        }
        finally
        {
            if (http.Request.Path.StartsWithSegments("/api") || http.Request.Path.StartsWithSegments("/sim-psp"))
            {
                var route = (http.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "(unmatched)";
                logger.LogInformation("HTTP {Method} {Route} responded {StatusCode} in {ElapsedMs} ms",
                    http.Request.Method, route, http.Response.StatusCode, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
        }
    }
}

internal sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    private const string Csp =
        "default-src 'self'; script-src 'self'; style-src 'self'; style-src-attr 'unsafe-inline'; " +
        "img-src 'self' data: blob:; font-src 'self'; connect-src 'self'; media-src 'self' blob:; " +
        "frame-ancestors 'none'; base-uri 'self'; form-action 'self'";

    public Task InvokeAsync(HttpContext http)
    {
        var headers = http.Response.Headers;
        headers.XContentTypeOptions = "nosniff";
        headers.XFrameOptions = "DENY";
        headers["Referrer-Policy"] = "no-referrer";
        headers["Permissions-Policy"] = "camera=(self), microphone=(), geolocation=()";
        headers.ContentSecurityPolicy = Csp;
        return next(http);
    }
}

public static class RequestBody
{
    /// <summary>Gövdeyi sınırla okur. İmza ve idempotency özeti ham baytlar üzerinden hesaplandığı için ham hali döner.</summary>
    public static async Task<byte[]> ReadRawAsync(HttpContext http, int maxBytes, CancellationToken ct)
    {
        if (http.Request.ContentLength > maxBytes)
            throw new ApiException(StatusCodes.Status413PayloadTooLarge, ErrorCodes.PayloadTooLarge, "The request body is too large.");

        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        int read;
        while ((read = await http.Request.Body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > maxBytes)
                throw new ApiException(StatusCodes.Status413PayloadTooLarge, ErrorCodes.PayloadTooLarge, "The request body is too large.");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    public static T Deserialize<T>(ReadOnlySpan<byte> utf8Json)
    {
        if (utf8Json.IsEmpty)
            throw ApiErrors.BadRequest(ErrorCodes.ValidationFailed, "A JSON request body is required.");
        try
        {
            return JsonSerializer.Deserialize<T>(utf8Json, JsonDefaults.Options)
                ?? throw ApiErrors.BadRequest(ErrorCodes.ValidationFailed, "A JSON request body is required.");
        }
        catch (JsonException e) when (e.Message.Contains("could not be mapped to any .NET member", StringComparison.Ordinal))
        {
            throw ApiErrors.BadRequest(ErrorCodes.UnknownField, "The request body contains a field that is not part of this operation.");
        }
        catch (JsonException)
        {
            throw ApiErrors.BadRequest(ErrorCodes.ValidationFailed, "The request body is not valid JSON for this operation.");
        }
    }
}
