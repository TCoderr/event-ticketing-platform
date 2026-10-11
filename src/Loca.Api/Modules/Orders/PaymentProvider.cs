using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Loca.Api.Infrastructure.Config;
using Loca.Api.Infrastructure.Outbox;
using Microsoft.Extensions.Options;

namespace Loca.Api.Modules.Orders;

public sealed class PspOptions
{
    public const string Section = "Psp";
    public const string HttpClientName = "psp";

    public string BaseUrl { get; set; } = "";
    public int TimeoutSeconds { get; set; } = 5;

    /// <summary>API anahtarı dosyadan okunur; satır içi değer yalnız test host'u içindir.</summary>
    public string? ApiKeyFile { get; set; }
    public string? ApiKey { get; set; }

    /// <summary>Aktif webhook secret'ları. Rotasyonda birden çok secret aynı anda geçerlidir.</summary>
    public List<string> WebhookSecretFiles { get; set; } = [];
    public List<string> WebhookSecrets { get; set; } = [];

    public int WebhookToleranceSeconds { get; set; } = 300;
    public int WebhookMaxBodyBytes { get; set; } = 16 * 1024;

    public int ReconcileIntervalSeconds { get; set; } = 15;
    public int ReconcileMaxAttempts { get; set; } = 8;
    public int ReconcileLeaseSeconds { get; set; } = 30;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(BaseUrl) && ResolveApiKey() is not null && ResolveWebhookSecrets().Count > 0;

    public string? ResolveApiKey() => !string.IsNullOrEmpty(ApiKey) ? ApiKey : SecretFile.ReadOptional(ApiKeyFile);

    public IReadOnlyList<byte[]> ResolveWebhookSecrets()
    {
        var secrets = new List<byte[]>();
        foreach (var inline in WebhookSecrets.Where(s => !string.IsNullOrEmpty(s))) secrets.Add(Encoding.UTF8.GetBytes(inline));
        foreach (var file in WebhookSecretFiles)
            if (SecretFile.ReadOptional(file) is { } value) secrets.Add(Encoding.UTF8.GetBytes(value));
        return secrets;
    }
}

public enum ProviderChargeStatus { Pending, Succeeded, Declined, Failed }
public enum ProviderRefundStatus { Succeeded, Failed }

public sealed record ProviderCharge(string ProviderPaymentId, ProviderChargeStatus Status, string? DeclineCode, int Sequence, long AmountMinor, string Currency);
public sealed record ProviderRefund(string ProviderRefundId, ProviderRefundStatus Status, string? FailureCode, long AmountMinor);

/// <summary>Sonuç bilinmiyor: zaman aşımı, ağ hatası veya 5xx. Ret DEĞİLDİR; yeniden deneme/sorgu gerekir.</summary>
public sealed class ProviderUnavailableException(string errorCode) : Exception($"Payment provider unavailable: {errorCode}"), IHasErrorCode
{
    public string ErrorCode { get; } = errorCode;
}

/// <summary>Sağlayıcı isteği kesin olarak reddetti (4xx): tekrar denemek sonucu değiştirmez.</summary>
public sealed class ProviderRejectedException(string errorCode) : Exception($"Payment provider rejected the request: {errorCode}"), IHasErrorCode
{
    public string ErrorCode { get; } = errorCode;
}

/// <summary>Sağlayıcı sınırı. Tüm çağrılar veritabanı transaction'ı DIŞINDA yapılır.</summary>
public interface IPaymentProvider
{
    /// <summary>Kararlı <paramref name="merchantReference"/> ile idempotent: tekrar çağrı yeni tahsilat yaratmaz.</summary>
    Task<ProviderCharge> CreateChargeAsync(string merchantReference, long amountMinor, string currency, string methodToken, CancellationToken ct);
    Task<ProviderCharge?> GetChargeAsync(string merchantReference, CancellationToken ct);
    Task<ProviderRefund> CreateRefundAsync(string merchantRefundReference, string chargeReference, long amountMinor, CancellationToken ct);
    Task<ProviderRefund?> GetRefundAsync(string merchantRefundReference, CancellationToken ct);
}

internal sealed class HttpPaymentProvider(IHttpClientFactory clients, IOptions<PspOptions> options) : IPaymentProvider
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record ChargeResponse(string Id, string Status, string? DeclineCode, int Sequence, long AmountMinor, string Currency);
    private sealed record RefundResponse(string Id, string Status, string? FailureCode, long AmountMinor);

    public async Task<ProviderCharge> CreateChargeAsync(string merchantReference, long amountMinor, string currency, string methodToken, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Post, "v1/charges", new { merchantReference, amountMinor, currency, methodToken }, ct);
        return ToCharge(await ReadAsync<ChargeResponse>(response, ct));
    }

    public async Task<ProviderCharge?> GetChargeAsync(string merchantReference, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, $"v1/charges/by-reference/{Uri.EscapeDataString(merchantReference)}", null, ct, notFoundIsNull: true);
        return response.StatusCode == HttpStatusCode.NotFound ? null : ToCharge(await ReadAsync<ChargeResponse>(response, ct));
    }

    public async Task<ProviderRefund> CreateRefundAsync(string merchantRefundReference, string chargeReference, long amountMinor, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Post, "v1/refunds", new { merchantReference = merchantRefundReference, chargeReference, amountMinor }, ct);
        return ToRefund(await ReadAsync<RefundResponse>(response, ct));
    }

    public async Task<ProviderRefund?> GetRefundAsync(string merchantRefundReference, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, $"v1/refunds/by-reference/{Uri.EscapeDataString(merchantRefundReference)}", null, ct, notFoundIsNull: true);
        return response.StatusCode == HttpStatusCode.NotFound ? null : ToRefund(await ReadAsync<RefundResponse>(response, ct));
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct, bool notFoundIsNull = false)
    {
        var opts = options.Value;
        var apiKey = opts.ResolveApiKey();
        if (string.IsNullOrWhiteSpace(opts.BaseUrl) || apiKey is null)
            throw new ProviderUnavailableException("provider_not_configured");

        var client = clients.CreateClient(PspOptions.HttpClientName);
        using var request = new HttpRequestMessage(method, new Uri(new Uri(opts.BaseUrl.TrimEnd('/') + "/"), path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        if (body is not null) request.Content = JsonContent.Create(body, options: Json);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(opts.TimeoutSeconds));
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ProviderUnavailableException("timeout");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Bağlantı kurulamadı veya yanıt gelmeden koptu: sağlayıcı isteği işlemiş olabilir.
            throw new ProviderUnavailableException("network_error");
        }

        if (response.IsSuccessStatusCode || (notFoundIsNull && response.StatusCode == HttpStatusCode.NotFound)) return response;

        var status = (int)response.StatusCode;
        response.Dispose();
        if (status >= 500 || status == 408 || status == 429) throw new ProviderUnavailableException($"http_{status}");
        throw new ProviderRejectedException($"http_{status}");
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(Json, ct) ?? throw new ProviderUnavailableException("empty_response");
        }
        catch (Exception e) when (e is JsonException or IOException or HttpRequestException)
        {
            // Yanıt okunamadı: sağlayıcı işlemi kaydetmiş olabilir. Sonuç bilinmiyor sayılır.
            throw new ProviderUnavailableException("unreadable_response");
        }
    }

    private static ProviderCharge ToCharge(ChargeResponse r) => new(r.Id, r.Status switch
    {
        "succeeded" => ProviderChargeStatus.Succeeded,
        "declined" => ProviderChargeStatus.Declined,
        "pending" => ProviderChargeStatus.Pending,
        "failed" => ProviderChargeStatus.Failed,
        _ => throw new ProviderUnavailableException("unknown_status"),
    }, r.DeclineCode, r.Sequence, r.AmountMinor, r.Currency);

    private static ProviderRefund ToRefund(RefundResponse r) => new(r.Id, r.Status switch
    {
        "succeeded" => ProviderRefundStatus.Succeeded,
        "failed" => ProviderRefundStatus.Failed,
        _ => throw new ProviderUnavailableException("unknown_status"),
    }, r.FailureCode, r.AmountMinor);
}

/// <summary>Demo ödeme yöntemi token'ları. Senaryoyu deterministik seçerler (ADR 0007).</summary>
public static class PaymentMethods
{
    public const string Success = "tok_success";
    public const string Decline = "tok_decline";
    public const string Transient = "tok_transient";
    public const string TimeoutSuccess = "tok_timeout_success";
    public const string TimeoutDecline = "tok_timeout_decline";
    public const string AsyncSuccess = "tok_async_success";
    public const string AsyncDecline = "tok_async_decline";
    public const string AsyncDuplicate = "tok_async_duplicate";
    public const string AsyncOutOfOrder = "tok_async_out_of_order";
    public const string LateSuccess = "tok_late_success";
    public const string RefundFail = "tok_refund_fail";
    public const string RefundTimeout = "tok_refund_timeout";
    public const string RefundTransient = "tok_refund_transient";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Success, Decline, Transient, TimeoutSuccess, TimeoutDecline, AsyncSuccess, AsyncDecline, AsyncDuplicate, AsyncOutOfOrder,
        LateSuccess, RefundFail, RefundTimeout, RefundTransient,
    };
}
