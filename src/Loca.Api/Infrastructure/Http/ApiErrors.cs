using System.Text.Json;
using System.Text.Json.Serialization;
using Loca.Domain;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace Loca.Api.Infrastructure.Http;

public static class JsonDefaults
{
    /// <summary>API'nin tek JSON ayarı: camelCase, metin enum, bilinmeyen alan reddi.</summary>
    public static readonly JsonSerializerOptions Options = Create();

    public static void Apply(JsonSerializerOptions o)
    {
        o.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        o.DictionaryKeyPolicy = null;
        o.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
        o.NumberHandling = JsonNumberHandling.Strict;
        o.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
        o.Converters.Add(new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false));
    }

    private static JsonSerializerOptions Create()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Apply(o);
        return o;
    }
}

/// <summary>İstemciye ProblemDetails olarak dönen, beklenen (iş kuralı / yetki / doğrulama) hata.</summary>
public sealed class ApiException(int status, string code, string detail, IReadOnlyDictionary<string, object?>? extensions = null)
    : Exception(detail)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
    public IReadOnlyDictionary<string, object?>? Extensions { get; } = extensions;
}

public static class ApiErrors
{
    public static ApiException NotFound(string detail = "Resource not found.") =>
        new(StatusCodes.Status404NotFound, ErrorCodes.NotFound, detail);

    public static ApiException Forbidden(string detail = "You do not have permission for this operation.") =>
        new(StatusCodes.Status403Forbidden, ErrorCodes.Forbidden, detail);

    public static ApiException Unauthenticated(string detail = "Authentication is required.") =>
        new(StatusCodes.Status401Unauthorized, ErrorCodes.Unauthenticated, detail);

    public static ApiException Conflict(string code, string detail, IReadOnlyDictionary<string, object?>? extensions = null) =>
        new(StatusCodes.Status409Conflict, code, detail, extensions);

    public static ApiException Unprocessable(string code, string detail, IReadOnlyDictionary<string, object?>? extensions = null) =>
        new(StatusCodes.Status422UnprocessableEntity, code, detail, extensions);

    public static ApiException BadRequest(string code, string detail, IReadOnlyDictionary<string, object?>? extensions = null) =>
        new(StatusCodes.Status400BadRequest, code, detail, extensions);

    public static ApiException Unavailable(string detail = "The service is temporarily unavailable. Retry the request.") =>
        new(StatusCodes.Status503ServiceUnavailable, ErrorCodes.ServiceUnavailable, detail);
}

/// <summary>Alan bazlı doğrulama hatalarını toplar; hata varsa tek 400 yanıtı üretir.</summary>
public sealed class Validator
{
    private readonly SortedDictionary<string, List<string>> _errors = new(StringComparer.Ordinal);

    public bool IsValid => _errors.Count == 0;

    public Validator Check(bool ok, string field, string message)
    {
        if (!ok)
        {
            if (!_errors.TryGetValue(field, out var list)) _errors[field] = list = [];
            list.Add(message);
        }
        return this;
    }

    public Validator Required(string? value, string field, int maxLength, int minLength = 1)
    {
        var trimmed = value?.Trim() ?? "";
        return Check(trimmed.Length >= minLength && trimmed.Length <= maxLength, field,
            $"Must be between {minLength} and {maxLength} characters.");
    }

    public void ThrowIfInvalid(string code = ErrorCodes.ValidationFailed)
    {
        if (IsValid) return;
        throw new ApiException(StatusCodes.Status400BadRequest, code, "One or more fields are invalid.",
            new Dictionary<string, object?> { ["errors"] = _errors.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray()) });
    }
}

public static class ProblemWriter
{
    private static readonly Dictionary<int, string> Titles = new()
    {
        [400] = "Bad Request", [401] = "Unauthorized", [403] = "Forbidden", [404] = "Not Found",
        [409] = "Conflict", [413] = "Payload Too Large", [415] = "Unsupported Media Type",
        [422] = "Unprocessable Content", [429] = "Too Many Requests", [500] = "Internal Server Error",
        [503] = "Service Unavailable",
    };

    public static ProblemDetails Build(HttpContext http, int status, string code, string detail,
        IReadOnlyDictionary<string, object?>? extensions = null)
    {
        var problem = new ProblemDetails
        {
            Type = $"https://loca.invalid/problems/{code}",
            Title = Titles.GetValueOrDefault(status, "Error"),
            Status = status,
            Detail = detail,
            Instance = http.Request.Path,
        };
        problem.Extensions["code"] = code;
        problem.Extensions["correlationId"] = CorrelationIdMiddleware.Get(http);
        if (extensions is not null)
            foreach (var (key, value) in extensions) problem.Extensions[key] = value;
        return problem;
    }

    public static async Task WriteAsync(HttpContext http, int status, string code, string detail,
        IReadOnlyDictionary<string, object?>? extensions = null)
    {
        if (http.Response.HasStarted) return;
        var problem = Build(http, status, code, detail, extensions);
        http.Response.StatusCode = status;
        http.Response.ContentType = "application/problem+json";
        http.Response.Headers.CacheControl = "no-store";
        await JsonSerializer.SerializeAsync(http.Response.Body, problem, ProblemJson, http.RequestAborted);
    }

    // ProblemDetails'in kendi dönüştürücüsü uzantıları üst düzeye yazar; camelCase yeterlidir.
    private static readonly JsonSerializerOptions ProblemJson = new(JsonSerializerDefaults.Web);
}

internal sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext http, Exception exception, CancellationToken ct)
    {
        switch (exception)
        {
            case ApiException api:
                await ProblemWriter.WriteAsync(http, api.Status, api.Code, api.Message, api.Extensions);
                return true;

            case DomainRuleException rule:
                var status = rule.Code switch
                {
                    ErrorCodes.InvalidStateTransition => StatusCodes.Status409Conflict,
                    ErrorCodes.UnsupportedCurrency or ErrorCodes.ValidationFailed => StatusCodes.Status400BadRequest,
                    _ => StatusCodes.Status422UnprocessableEntity,
                };
                await ProblemWriter.WriteAsync(http, status, rule.Code, rule.Message);
                return true;

            case AntiforgeryValidationException:
                await ProblemWriter.WriteAsync(http, StatusCodes.Status403Forbidden, ErrorCodes.CsrfInvalid,
                    "The CSRF token is missing or invalid. Fetch a new token and retry.");
                return true;

            case BadHttpRequestException bad:
                var (code, detail) = DescribeBadRequest(bad);
                await ProblemWriter.WriteAsync(http, bad.StatusCode is >= 400 and < 500 ? bad.StatusCode : 400, code, detail);
                return true;

            case JsonException:
                await ProblemWriter.WriteAsync(http, StatusCodes.Status400BadRequest, ErrorCodes.ValidationFailed,
                    "The request body is not valid JSON for this operation.");
                return true;

            case OperationCanceledException when http.RequestAborted.IsCancellationRequested:
                return true; // İstemci bağlantıyı kapattı; yazılacak yanıt yok.
        }

        if (IsDatabaseUnavailable(exception))
        {
            logger.LogError("Database unavailable while handling {Method} {Path}: {ExceptionType}",
                http.Request.Method, http.Request.Path.Value, exception.GetType().Name);
            http.Response.Headers.RetryAfter = "2";
            await ProblemWriter.WriteAsync(http, StatusCodes.Status503ServiceUnavailable, ErrorCodes.ServiceUnavailable,
                "The service is temporarily unavailable. The outcome of this request is unknown; retry with the same Idempotency-Key.");
            return true;
        }

        // İstisna mesajı yanıta yazılmaz; log'da da yalnız tür ve yığın izi bulunur (mesaj veri içerebilir).
        logger.LogError("Unhandled {ExceptionType} while handling {Method} {Path}\n{StackTrace}",
            exception.GetType().FullName, http.Request.Method, http.Request.Path.Value, exception.StackTrace);
        await ProblemWriter.WriteAsync(http, StatusCodes.Status500InternalServerError, ErrorCodes.InternalError,
            "An unexpected error occurred.");
        return true;
    }

    private static (string Code, string Detail) DescribeBadRequest(BadHttpRequestException bad)
    {
        if (bad.StatusCode == StatusCodes.Status413PayloadTooLarge)
            return (ErrorCodes.PayloadTooLarge, "The request body is too large.");
        if (bad.InnerException is JsonException json &&
            json.Message.Contains("could not be mapped to any .NET member", StringComparison.Ordinal))
            return (ErrorCodes.UnknownField, "The request body contains a field that is not part of this operation.");
        return (ErrorCodes.ValidationFailed, "The request could not be read. Check the body, route and query values.");
    }

    public static bool IsDatabaseUnavailable(Exception exception)
    {
        for (var e = exception; e is not null; e = e.InnerException)
        {
            if (e is PostgresException pg)
            {
                // 08xxx bağlantı; 57Pxx yönetici kapatması/başlatılıyor; 53xxx kaynak yetersizliği.
                if (pg.SqlState.StartsWith("08", StringComparison.Ordinal) ||
                    pg.SqlState.StartsWith("57P", StringComparison.Ordinal) ||
                    pg.SqlState.StartsWith("53", StringComparison.Ordinal))
                    return true;
                continue;
            }
            if (e is NpgsqlException or System.Net.Sockets.SocketException or TimeoutException) return true;
        }
        return false;
    }
}
