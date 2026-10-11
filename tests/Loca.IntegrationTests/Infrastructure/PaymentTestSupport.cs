using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Loca.Domain;

namespace Loca.IntegrationTests.Infrastructure;

/// <summary>
/// Uygulama ile simüle sağlayıcı arasındaki HTTP sınırına hata enjekte eder. "Yanıtı düşür" kipinde istek
/// sağlayıcıya ULAŞIR ve işlenir; yalnız yanıt uygulamaya dönmez — gerçek "işlendi ama yanıt kayboldu" durumu.
/// </summary>
public sealed class ProviderFaults
{
    private int _dropResponses;
    private int _refuseRequests;
    private int _respondCount;
    private HttpStatusCode _respondWith;
    private int _calls;

    public int Calls => Volatile.Read(ref _calls);

    /// <summary>Sonraki <paramref name="count"/> çağrıda: sağlayıcı işler, yanıt kaybolur.</summary>
    public void DropResponses(int count) => Interlocked.Exchange(ref _dropResponses, count);

    /// <summary>Sonraki <paramref name="count"/> çağrıda: istek sağlayıcıya hiç ulaşmaz.</summary>
    public void RefuseRequests(int count) => Interlocked.Exchange(ref _refuseRequests, count);

    /// <summary>Sonraki <paramref name="count"/> çağrıda: sağlayıcıya ulaşmadan bu durum kodu döner.</summary>
    public void RespondWith(HttpStatusCode status, int count)
    {
        _respondWith = status;
        Interlocked.Exchange(ref _respondCount, count);
    }

    public void Clear()
    {
        DropResponses(0);
        RefuseRequests(0);
        Interlocked.Exchange(ref _respondCount, 0);
    }

    internal enum Fault { None, Refuse, Drop, Respond }

    internal (Fault Fault, HttpStatusCode Status) Next()
    {
        Interlocked.Increment(ref _calls);
        if (TryTake(ref _refuseRequests)) return (Fault.Refuse, default);
        if (TryTake(ref _respondCount)) return (Fault.Respond, _respondWith);
        if (TryTake(ref _dropResponses)) return (Fault.Drop, default);
        return (Fault.None, default);
    }

    private static bool TryTake(ref int counter)
    {
        while (true)
        {
            var current = Volatile.Read(ref counter);
            if (current <= 0) return false;
            if (Interlocked.CompareExchange(ref counter, current - 1, current) == current) return true;
        }
    }
}

internal sealed class FaultInjectingHandler(ProviderFaults faults, HttpMessageHandler inner) : DelegatingHandler(inner)
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var (fault, status) = faults.Next();
        switch (fault)
        {
            case ProviderFaults.Fault.Refuse:
                throw new HttpRequestException("Injected: connection refused.");
            case ProviderFaults.Fault.Respond:
                return new HttpResponseMessage(status) { RequestMessage = request, Content = new StringContent("{}") };
            case ProviderFaults.Fault.Drop:
                using (await base.SendAsync(request, ct)) { /* Sağlayıcı işledi; yanıt bilerek atılır. */ }
                throw new HttpRequestException("Injected: response lost after the provider processed the request.");
            default:
                return await base.SendAsync(request, ct);
        }
    }
}

public static class PurchaseApi
{
    public static Task<ApiResponse> CheckoutAsync(this TestClient client, Guid holdId, string method = "tok_success", string? key = null) =>
        client.PostAsync($"/api/holds/{holdId}/checkout", new { paymentMethod = method }, key ?? HoldApi.NewKey());

    public static Guid OrderId(this ApiResponse response) => response.ShouldBe(HttpStatusCode.Accepted).Json["id"]!.GetValue<Guid>();

    public static async Task<JsonNode> OrderAsync(this TestClient client, Guid orderId) =>
        (await client.GetAsync($"/api/orders/{orderId}")).ShouldBe(HttpStatusCode.OK).Json;

    public static string OrderStatus(this JsonNode order) => order["status"]!.GetValue<string>();
    public static string PaymentStatus(this JsonNode order) => order["payment"]!["status"]!.GetValue<string>();
    public static string? RefundStatus(this JsonNode order) => order["refund"]?["status"]?.GetValue<string>();

    public static string MerchantReference(Guid orderId) => $"pay_{orderId:N}";
}

/// <summary>Elle imzalanmış sağlayıcı olayı gönderir (sahte/tekrarlı/sırasız webhook testleri için).</summary>
public static class WebhookSender
{
    public static byte[] Event(string eventId, string type, Guid orderId, long amountMinor, int sequence, string currency = "TRY", string? declineCode = null) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            id = eventId, type, created = 0, sequence,
            data = new { merchantReference = PurchaseApi.MerchantReference(orderId), providerPaymentId = "ch_test", amountMinor, currency, declineCode },
        });

    public static Task<ApiResponse> SendAsync(TestClient anonymous, byte[] body, DateTimeOffset signedAt, string? secret = LocaFactory.WebhookSecret, string? signatureOverride = null)
    {
        var headers = new Dictionary<string, string>();
        var signature = signatureOverride ?? (secret is null ? null : WebhookSignature.Sign(Encoding.UTF8.GetBytes(secret), signedAt.ToUnixTimeSeconds(), body));
        if (signature is not null) headers["X-Psp-Signature"] = signature;
        return anonymous.SendAsync(HttpMethod.Post, "/api/webhooks/psp", csrf: false, headers: headers, rawBody: body);
    }
}
