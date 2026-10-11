using System.Security.Cryptography;
using System.Text.Json;
using Loca.Api.Infrastructure.Http;
using Loca.Api.Infrastructure.Persistence;
using Loca.Api.Infrastructure.RateLimiting;
using Loca.Api.Infrastructure.Time;
using Loca.Api.Infrastructure.Tx;
using Loca.Api.Modules.Operations;
using Loca.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Loca.Api.Modules.Orders;

internal static class WebhookEndpoint
{
    public const string Source = "psp";
    public const string SignatureHeader = "X-Psp-Signature";

    private sealed record EventData(string? MerchantReference, string? ProviderPaymentId, long? AmountMinor, string? Currency, string? DeclineCode);
    private sealed record Event(string? Id, string? Type, long? Created, int? Sequence, EventData? Data);

    private static readonly JsonSerializerOptions EventJson = new(JsonSerializerDefaults.Web);

    public static void Map(IEndpointRouteBuilder app)
    {
        // Çerez/CSRF yoktur; kimlik doğrulama ham gövde üzerindeki HMAC imzasıdır.
        app.MapPost("/api/webhooks/psp", HandleAsync).AllowAnonymous().RequireRateLimiting(RateLimitPolicies.Webhook).WithTags("Webhooks");
    }

    private static async Task<IResult> HandleAsync(HttpContext http, AppDbContext db, IClock clock, ITransactionRunner transactions,
        PaymentService payments, IAuditWriter audit, IOptions<PspOptions> options, CancellationToken ct)
    {
        var opts = options.Value;

        // 1) Ham baytlar: imza, ayrıştırılıp yeniden serileştirilmiş JSON üzerinden DEĞİL, gelen baytlar üzerinden doğrulanır.
        var raw = await RequestBody.ReadRawAsync(http, opts.WebhookMaxBodyBytes, ct);
        var now = await clock.NowAsync(ct);
        var verification = WebhookSignature.Verify(http.Request.Headers[SignatureHeader].ToString(), raw, [.. opts.ResolveWebhookSecrets()],
            now, TimeSpan.FromSeconds(opts.WebhookToleranceSeconds));
        switch (verification)
        {
            case WebhookVerification.Malformed:
                throw ApiErrors.BadRequest(ErrorCodes.WebhookMalformed, "The signature header is missing or malformed.");
            case WebhookVerification.SignatureInvalid:
                throw new ApiException(StatusCodes.Status401Unauthorized, ErrorCodes.WebhookSignatureInvalid, "The webhook signature is not valid.");
            case WebhookVerification.TimestampOutOfRange:
                throw new ApiException(StatusCodes.Status401Unauthorized, ErrorCodes.WebhookTimestampOutOfRange, "The webhook timestamp is outside the accepted window.");
        }

        // 2) İmza geçerli: içerik artık ayrıştırılabilir.
        Event evt;
        try
        {
            evt = JsonSerializer.Deserialize<Event>(raw, EventJson) ?? throw new JsonException();
        }
        catch (JsonException)
        {
            throw ApiErrors.BadRequest(ErrorCodes.WebhookMalformed, "The webhook body is not valid JSON.");
        }
        if (evt.Id is not { Length: > 0 and <= 80 } eventId || evt.Type is not { Length: > 0 and <= 60 } type || evt.Sequence is not { } sequence ||
            evt.Data is not { MerchantReference: { Length: > 0 } reference, AmountMinor: { } amountMinor, Currency: { Length: > 0 } currency } data)
            throw ApiErrors.BadRequest(ErrorCodes.WebhookMalformed, "The webhook body is missing required fields.");

        var payloadHash = Convert.ToHexStringLower(SHA256.HashData(raw));
        ProviderChargeStatus? status = type switch
        {
            "charge.succeeded" => ProviderChargeStatus.Succeeded,
            "charge.declined" => ProviderChargeStatus.Declined,
            "charge.pending" => ProviderChargeStatus.Pending,
            "charge.failed" => ProviderChargeStatus.Failed,
            _ => null,
        };

        // 3) Dedupe + iş etkisi tek transaction'da: ya ikisi birden commit olur ya hiçbiri.
        var (conflict, outcome) = await transactions.RunAsync(async token =>
        {
            var received = await clock.NowAsync(token);
            var inserted = await db.Database.ExecuteSqlAsync($"""
                INSERT INTO ops.inbox_messages (source, event_id, payload_hash, type, received_at, outcome)
                VALUES ({Source}, {eventId}, {payloadHash}, {type}, {received}, 'Processing')
                ON CONFLICT (source, event_id) DO NOTHING
                """, token);
            if (inserted == 0)
            {
                var existingHash = await db.InboxMessages.AsNoTracking().Where(m => m.Source == Source && m.EventId == eventId).Select(m => m.PayloadHash).FirstAsync(token);
                if (string.Equals(existingHash, payloadHash, StringComparison.Ordinal))
                    return (false, "duplicate"); // Aynı olay yeniden teslim edildi: etki yok.

                // Aynı kimlik, farklı içerik: uygulanmaz, çakışma olarak kaydedilir.
                audit.Add(received, "webhook.event_conflict", "inbox", eventId, data: new { type }, actorKind: "Provider");
                await db.SaveChangesAsync(token);
                return (true, "conflict");
            }

            var result = status is null
                ? "ignored_unknown_type"
                : (await payments.ApplyChargeStateInTransactionAsync(reference,
                    new ProviderCharge(data.ProviderPaymentId ?? "", status.Value, data.DeclineCode, sequence, amountMinor, currency), "webhook", token)) switch
                {
                    ApplyOutcome.Applied => "applied",
                    ApplyOutcome.Stale => "ignored_stale",
                    ApplyOutcome.AlreadyFinal => "ignored_already_final",
                    ApplyOutcome.Mismatch => "mismatch",
                    _ => "ignored_unknown_reference",
                };
            await db.InboxMessages.Where(m => m.Source == Source && m.EventId == eventId).ExecuteUpdateAsync(u => u.SetProperty(m => m.Outcome, result), token);
            await db.SaveChangesAsync(token);
            return (false, result);
        }, ct);

        if (conflict)
            throw ApiErrors.Conflict(ErrorCodes.WebhookEventConflict, "An event with this id was already received with different content.");
        return Results.Ok(new { received = true, outcome });
    }
}
