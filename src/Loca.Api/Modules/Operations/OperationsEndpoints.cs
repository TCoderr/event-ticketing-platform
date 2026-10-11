using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Loca.Api.Infrastructure.Auth;
using Loca.Api.Infrastructure.Http;
using Loca.Api.Infrastructure.Metrics;
using Loca.Api.Infrastructure.Outbox;
using Loca.Api.Infrastructure.Persistence;
using Loca.Api.Infrastructure.Time;
using Loca.Api.Infrastructure.Tx;
using Loca.Api.Infrastructure.Workers;
using Loca.Api.Modules.Catalog;
using Loca.Api.Modules.Orders;
using Loca.Api.PspSimulator;
using Loca.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Loca.Api.Modules.Operations;

/// <summary>Seans satış ve kapasite özeti. Metrik tanımları: docs/01-product-requirements.md §5.</summary>
public sealed record SessionSummaryDto(Guid SessionId, string EventTitle, SessionStatus Status, SalesMode SalesMode, string Currency,
    int Capacity, int Sold, int Held, int Available, int TicketsSold, int TicketsCheckedIn,
    long GrossCapturedMinor, long RefundedMinor, long RefundPendingMinor, long NetMinor,
    int OrdersPaid, int OrdersCancelled, int OrdersPendingPayment, DateTimeOffset ServerTime);

public sealed record OperationItemDto(string Kind, Guid Id, Guid? OrderId, string? OrderNumber, Guid SessionId, string EventTitle, string Status,
    string? Detail, long AmountMinor, bool LateSuccess, int AttemptCount, DateTimeOffset UpdatedAt, IReadOnlyList<string> Actions);
public sealed record OperationRunDto(Guid Id, string TargetType, Guid TargetId, ReconciliationOutcome Outcome, string DetailCode, DateTimeOffset CreatedAt);
public sealed record OperationCountsDto(int UnknownPayments, int PendingPayments, int ReviewPayments, int LateSuccesses, int PendingRefunds, int FailedRefunds,
    int PendingCancellationJobs, int RunsSucceeded, int RunsFailed, int RunsAttempted, int RunsPlanned);
public sealed record OperationsDto(OperationCountsDto Counts, IReadOnlyList<OperationItemDto> Items, IReadOnlyList<OperationRunDto> RecentRuns, DateTimeOffset ServerTime);
public sealed record AuditDto(long Id, DateTimeOffset OccurredAt, string ActorKind, Guid? ActorUserId, string Action, string EntityType, string EntityId, Guid? OrganizationId);
public sealed record DeadLetterDto(Guid Id, string Type, int AttemptCount, string? LastErrorCode, DateTimeOffset CreatedAt, DateTimeOffset? DeadLetteredAt);
public sealed record AdminEventDto(Guid Id, string Title, string Slug, string OrganizationName, EventStatus Status, bool ModerationBlocked, bool IsDemo);
public sealed record DemoPspResolveRequest(string Outcome);

public sealed class OperationsQueries(AppDbContext db, IClock clock)
{
    public async Task<SessionSummaryDto> SessionSummaryAsync(Guid sessionId, CancellationToken ct)
    {
        var session = await db.Sessions.AsNoTracking().Where(s => s.Id == sessionId)
            .Select(s => new { s.Id, s.Status, s.SalesMode, EventTitle = s.Event.Title }).FirstOrDefaultAsync(ct) ?? throw ApiErrors.NotFound();
        var now = await clock.NowAsync(ct);

        int capacity, sold, held;
        if (session.SalesMode == SalesMode.ReservedSeating)
        {
            var seats = await db.SessionSeats.AsNoTracking().Where(s => s.SessionId == sessionId)
                .GroupBy(_ => 1).Select(g => new
                {
                    Total = g.Count(), Sold = g.Count(s => s.Status == SeatStatus.Sold),
                    Held = g.Count(s => s.Status == SeatStatus.Held && s.HeldUntil > now), // süresi dolmuş hold "tutuluyor" sayılmaz
                }).FirstOrDefaultAsync(ct);
            (capacity, sold, held) = (seats?.Total ?? 0, seats?.Sold ?? 0, seats?.Held ?? 0);
        }
        else
        {
            var categories = await db.GaCategories.AsNoTracking().Where(c => c.SessionId == sessionId)
                .GroupBy(_ => 1).Select(g => new { Capacity = g.Sum(c => c.Capacity), Sold = g.Sum(c => c.SoldCount) }).FirstOrDefaultAsync(ct);
            // Sayaçtaki held değeri henüz toplanmamış süresi dolmuş hold'ları içerebilir; özet yalnız aktif olanları sayar.
            var activeHeld = await db.ReservationLines.AsNoTracking()
                .Where(l => l.Kind == ReservationLineKind.GeneralAdmission &&
                            db.Reservations.Any(r => r.Id == l.ReservationId && r.SessionId == sessionId && r.Status == ReservationStatus.Active && r.ExpiresAt > now))
                .SumAsync(l => (int?)l.Quantity, ct) ?? 0;
            (capacity, sold, held) = (categories?.Capacity ?? 0, categories?.Sold ?? 0, activeHeld);
        }

        var tickets = await db.Tickets.AsNoTracking().Where(t => t.SessionId == sessionId && t.Status != TicketStatus.Cancelled)
            .GroupBy(_ => 1).Select(g => new { Sold = g.Count(), CheckedIn = g.Count(t => t.Status == TicketStatus.CheckedIn) }).FirstOrDefaultAsync(ct);

        var gross = await db.Payments.AsNoTracking().Where(p => p.Order.SessionId == sessionId && p.Status == PaymentStatus.Succeeded).SumAsync(p => (long?)p.AmountMinor, ct) ?? 0;
        var refunds = await db.Refunds.AsNoTracking().Where(r => r.Payment.Order.SessionId == sessionId)
            .GroupBy(r => r.Status).Select(g => new { Status = g.Key, Amount = g.Sum(r => r.AmountMinor) }).ToListAsync(ct);
        var refunded = refunds.Where(r => r.Status == RefundStatus.Succeeded).Sum(r => r.Amount);
        var refundPending = refunds.Where(r => r.Status != RefundStatus.Succeeded).Sum(r => r.Amount);

        var orders = await db.Orders.AsNoTracking().Where(o => o.SessionId == sessionId).GroupBy(o => o.Status).Select(g => new { Status = g.Key, Count = g.Count() }).ToListAsync(ct);
        int Orders(OrderStatus status) => orders.FirstOrDefault(o => o.Status == status)?.Count ?? 0;

        return new SessionSummaryDto(session.Id, session.EventTitle, session.Status, session.SalesMode, Money.SupportedCurrency,
            capacity, sold, held, Math.Max(0, capacity - sold - held), tickets?.Sold ?? 0, tickets?.CheckedIn ?? 0,
            gross, refunded, refundPending, gross - refunded,
            Orders(OrderStatus.Paid), Orders(OrderStatus.Cancelled), Orders(OrderStatus.PendingPayment), now);
    }

    /// <param name="organizationId">Organizatör görünümü için zorunlu filtre; platform yöneticisi için null (tümü).</param>
    public async Task<OperationsDto> OperationsAsync(Guid? organizationId, CancellationToken ct)
    {
        var now = await clock.NowAsync(ct);

        var payments = await (from p in db.Payments.AsNoTracking()
                              join s in db.Sessions on p.Order.SessionId equals s.Id
                              where (organizationId == null || p.Order.OrganizationId == organizationId) &&
                                    (p.Status == PaymentStatus.Unknown || p.Status == PaymentStatus.Pending || p.Status == PaymentStatus.Initiated ||
                                     p.ReviewReason != null ||
                                     (p.LateSuccessAt != null && (p.Refund == null || p.Refund.Status != RefundStatus.Succeeded)))
                              orderby p.UpdatedAt descending, p.Id
                              select new
                              {
                                  p.Id, p.OrderId, p.Order.OrderNumber, SessionId = s.Id, EventTitle = s.Event.Title, p.Status, p.FailureCode, p.ReviewReason,
                                  p.AmountMinor, p.LateSuccessAt, p.AttemptCount, p.UpdatedAt,
                              }).Take(200).ToListAsync(ct);

        var refunds = await (from r in db.Refunds.AsNoTracking()
                             join s in db.Sessions on r.Payment.Order.SessionId equals s.Id
                             where (organizationId == null || r.Payment.Order.OrganizationId == organizationId) && r.Status != RefundStatus.Succeeded
                             orderby r.UpdatedAt descending, r.Id
                             select new
                             {
                                 r.Id, r.OrderId, r.Payment.Order.OrderNumber, SessionId = s.Id, EventTitle = s.Event.Title, r.Status, r.LastErrorCode, r.Reason,
                                 r.AmountMinor, r.AttemptCount, r.UpdatedAt,
                             }).Take(200).ToListAsync(ct);

        var jobs = await (from j in db.SessionCancellationJobs.AsNoTracking()
                          join s in db.Sessions on j.SessionId equals s.Id
                          where (organizationId == null || j.OrganizationId == organizationId) && j.Status == CancellationJobStatus.Pending
                          orderby j.CreatedAt
                          select new { j.SessionId, EventTitle = s.Event.Title, j.ProcessedCount, j.CreatedAt }).Take(50).ToListAsync(ct);

        var runsQuery = db.ReconciliationRuns.AsNoTracking().Where(r => organizationId == null || r.OrganizationId == organizationId);
        var recentRuns = await runsQuery.OrderByDescending(r => r.CreatedAt).ThenBy(r => r.Id).Take(20)
            .Select(r => new OperationRunDto(r.Id, r.TargetType, r.TargetId, r.Outcome, r.DetailCode, r.CreatedAt)).ToListAsync(ct);
        var runCounts = await runsQuery.GroupBy(r => r.Outcome).Select(g => new { Outcome = g.Key, Count = g.Count() }).ToListAsync(ct);
        int Runs(ReconciliationOutcome outcome) => runCounts.FirstOrDefault(r => r.Outcome == outcome)?.Count ?? 0;

        var items = new List<OperationItemDto>();
        foreach (var p in payments)
        {
            // Sonucu bilinmeyen ödeme "reconcile" edilebilir; geç başarı yalnız bilgi amaçlı listelenir (telafisi iade satırındadır).
            var open = p.Status is PaymentStatus.Unknown or PaymentStatus.Pending or PaymentStatus.Initiated;
            items.Add(new OperationItemDto("payment", p.Id, p.OrderId, p.OrderNumber, p.SessionId, p.EventTitle, p.Status.ToString(),
                p.ReviewReason ?? p.FailureCode, p.AmountMinor, p.LateSuccessAt is not null, p.AttemptCount, p.UpdatedAt, open ? ["reconcile"] : []));
        }
        foreach (var r in refunds)
            items.Add(new OperationItemDto("refund", r.Id, r.OrderId, r.OrderNumber, r.SessionId, r.EventTitle, r.Status.ToString(),
                r.LastErrorCode ?? r.Reason.ToString(), r.AmountMinor, r.Reason == RefundReason.LatePaymentCompensation, r.AttemptCount, r.UpdatedAt,
                r.Status == RefundStatus.Failed ? ["retry"] : []));
        foreach (var j in jobs)
            items.Add(new OperationItemDto("cancellation", j.SessionId, null, null, j.SessionId, j.EventTitle, "Pending", $"processed:{j.ProcessedCount}", 0, false, 0, j.CreatedAt, []));

        var counts = new OperationCountsDto(
            payments.Count(p => p.Status == PaymentStatus.Unknown),
            payments.Count(p => p.Status is PaymentStatus.Pending or PaymentStatus.Initiated),
            payments.Count(p => p.ReviewReason is not null),
            payments.Count(p => p.LateSuccessAt is not null),
            refunds.Count(r => r.Status == RefundStatus.Pending),
            refunds.Count(r => r.Status == RefundStatus.Failed),
            jobs.Count,
            // Yalnız gerçekten başarıyla sonuçlanan denemeler "başarılı" sayılır.
            Runs(ReconciliationOutcome.Succeeded), Runs(ReconciliationOutcome.Failed), Runs(ReconciliationOutcome.Attempted), Runs(ReconciliationOutcome.Planned));

        return new OperationsDto(counts, items, recentRuns, now);
    }
}

internal static class OperationsEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var organizer = app.MapGroup("/api/organizer").RequireAuthorization().WithTags("Operations");

        organizer.MapGet("/sessions/{sessionId:guid}/summary", async (Guid sessionId, HttpContext http, CatalogAccess access, OperationsQueries queries, CancellationToken ct) =>
        {
            await access.RequireSessionAdminAsync(sessionId, ct);
            http.Response.Headers.CacheControl = "no-store";
            return Results.Ok(await queries.SessionSummaryAsync(sessionId, ct));
        });

        organizer.MapGet("/organizations/{organizationId:guid}/operations", async (Guid organizationId, HttpContext http, IOrganizationAccess access,
            OperationsQueries queries, CancellationToken ct) =>
        {
            await access.RequireAdminAsync(organizationId, ct);
            http.Response.Headers.CacheControl = "no-store";
            return Results.Ok(await queries.OperationsAsync(organizationId, ct));
        });

        var admin = app.MapGroup("/api/admin").RequireAuthorization().WithTags("Admin");

        admin.MapGet("/operations", async (HttpContext http, ICurrentUser user, OperationsQueries queries, CancellationToken ct) =>
        {
            user.RequirePlatformAdmin();
            http.Response.Headers.CacheControl = "no-store";
            return Results.Ok(await queries.OperationsAsync(null, ct));
        });

        admin.MapGet("/events", async (ICurrentUser user, AppDbContext db, CancellationToken ct) =>
        {
            user.RequirePlatformAdmin();
            return Results.Ok(await db.Events.AsNoTracking().OrderByDescending(e => e.CreatedAt).ThenBy(e => e.Id).Take(200)
                .Select(e => new AdminEventDto(e.Id, e.Title, e.Slug, e.Organization.Name, e.Status, e.ModerationBlockedAt != null, e.IsDemo)).ToListAsync(ct));
        });

        admin.MapGet("/audit", async (ICurrentUser user, AppDbContext db, int? limit, CancellationToken ct) =>
        {
            user.RequirePlatformAdmin();
            var take = Math.Clamp(limit ?? 100, 1, 500);
            // Denetim kaydının "data" alanı kişisel veri içermez; yine de liste yalnız özet alanları döndürür.
            return Results.Ok(await db.AuditLog.AsNoTracking().OrderByDescending(a => a.Id).Take(take)
                .Select(a => new AuditDto(a.Id, a.OccurredAt, a.ActorKind, a.ActorUserId, a.Action, a.EntityType, a.EntityId, a.OrganizationId)).ToListAsync(ct));
        });

        admin.MapGet("/outbox/dead-letters", async (ICurrentUser user, AppDbContext db, CancellationToken ct) =>
        {
            user.RequirePlatformAdmin();
            return Results.Ok(await db.OutboxMessages.AsNoTracking().Where(m => m.DeadLetteredAt != null).OrderByDescending(m => m.DeadLetteredAt).Take(200)
                .Select(m => new DeadLetterDto(m.Id, m.Type, m.AttemptCount, m.LastErrorCode, m.CreatedAt, m.DeadLetteredAt)).ToListAsync(ct));
        });

        admin.MapPost("/outbox/{messageId:guid}/replay", async (Guid messageId, ICurrentUser user, AppDbContext db, IClock clock, IAuditWriter audit,
            WorkerSignal<OutboxProcessor> signal, CancellationToken ct) =>
        {
            user.RequirePlatformAdmin();
            var now = await clock.NowAsync(ct);
            // Koşullu güncelleme: yalnız dead-letter durumundaki mesaj yeniden kuyruğa alınır (çift tıklama ikinci kez etkilemez).
            var replayed = await db.OutboxMessages.Where(m => m.Id == messageId && m.DeadLetteredAt != null)
                .ExecuteUpdateAsync(u => u
                    .SetProperty(m => m.DeadLetteredAt, (DateTimeOffset?)null)
                    .SetProperty(m => m.AttemptCount, 0)
                    .SetProperty(m => m.AvailableAt, now)
                    .SetProperty(m => m.LastErrorCode, (string?)null), ct);
            if (replayed != 1) throw ApiErrors.NotFound("No dead-lettered message has this id.");
            audit.Add(now, "outbox.replayed", "outbox", messageId);
            await db.SaveChangesAsync(ct);
            signal.Notify();
            return Results.Ok(new { id = messageId, requeued = true });
        });

        admin.MapGet("/metrics", (ICurrentUser user, AppMetrics metrics, TransactionStats transactions) =>
        {
            user.RequirePlatformAdmin();
            var snapshot = new SortedDictionary<string, long>(metrics.Snapshot().ToDictionary(kv => kv.Key, kv => kv.Value), StringComparer.Ordinal)
            {
                ["loca_transaction_retries_total"] = transactions.Retries,
            };
            return Results.Ok(snapshot);
        });

        // Demo PSP paneli: gerçek bir sağlayıcının yönetim panelinin karşılığı. Yalnız simülatör açıkken ve yalnız platform yöneticisine.
        if (app.ServiceProvider.GetRequiredService<IConfiguration>().GetValue<bool>($"{PspSimulatorOptions.Section}:Enabled"))
        {
            admin.MapGet("/demo-psp/charges", async (ICurrentUser user, PspSimulatorPanel panel, string? status, CancellationToken ct) =>
            {
                user.RequirePlatformAdmin();
                return Results.Json(await panel.ListChargesAsync(status, ct));
            });
            admin.MapPost("/demo-psp/charges/{reference}/resolve", async (string reference, DemoPspResolveRequest request, ICurrentUser user,
                PspSimulatorPanel panel, IClock clock, IAuditWriter audit, AppDbContext db, CancellationToken ct) =>
            {
                user.RequirePlatformAdmin();
                if (request.Outcome is not ("succeeded" or "declined"))
                    throw ApiErrors.BadRequest(ErrorCodes.ValidationFailed, "Outcome must be 'succeeded' or 'declined'.");
                var result = await panel.ResolveAsync(reference, request.Outcome, ct) ?? throw ApiErrors.NotFound();
                audit.Add(await clock.NowAsync(ct), "demo_psp.charge_resolved", "payment_reference", reference, data: new { request.Outcome });
                await db.SaveChangesAsync(ct);
                return Results.Json(result);
            });
        }
    }
}

/// <summary>Demo PSP panelinin simülatöre erişimi: uygulamanın geri kalanı gibi yalnız HTTP + API anahtarı ile.</summary>
public sealed class PspSimulatorPanel(IHttpClientFactory clients, IOptions<PspOptions> options)
{
    public async Task<JsonNode?> ListChargesAsync(string? status, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, "v1/charges" + (status is null ? "" : $"?status={Uri.EscapeDataString(status)}"), null, ct);
        response.EnsureSuccessStatusCode();
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
    }

    public async Task<JsonNode?> ResolveAsync(string reference, string outcome, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Post, $"v1/charges/by-reference/{Uri.EscapeDataString(reference)}/resolve", new { outcome }, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        var opts = options.Value;
        var request = new HttpRequestMessage(method, new Uri(new Uri(opts.BaseUrl.TrimEnd('/') + "/"), path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", opts.ResolveApiKey());
        if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body, JsonDefaults.Options), System.Text.Encoding.UTF8, "application/json");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(opts.TimeoutSeconds));
        try
        {
            return await clients.CreateClient(PspOptions.HttpClientName).SendAsync(request, timeout.Token);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            throw ApiErrors.Unavailable("The demo payment provider is not reachable.");
        }
    }
}
