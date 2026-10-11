using Loca.Api.Infrastructure.Config;
using Loca.Api.Infrastructure.Http;
using Loca.Api.Infrastructure.Outbox;
using Loca.Api.Infrastructure.Persistence;
using Loca.Api.Infrastructure.Time;
using Loca.Api.Infrastructure.Tx;
using Loca.Api.Infrastructure.Workers;
using Loca.Api.Modules.Catalog;
using Loca.Api.Modules.Identity;
using Loca.Api.Modules.Inventory;
using Loca.Api.Modules.Orders;
using Loca.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Loca.Api.Modules.Operations;

public sealed class DemoCrowdOptions
{
    public const string Section = "Demo:Crowd";
    public bool Enabled { get; set; } = true;
    public int IntervalSeconds { get; set; } = 20;

    /// <summary>Müsaitlik bu oranın altına inince sentetik alım durur; gerçek kullanıcıya yer kalır.</summary>
    public double StopBelowAvailableRatio { get; set; } = 0.5;
}

/// <summary>
/// Demo simülatörü: yalnız <c>Demo:SimulatorEnabled</c> açıkken ve yalnız organizatörün seans bazında açtığı
/// (<c>demo_simulator_enabled</c>) seanslarda, sentetik "demo izleyici" hesaplarıyla alım üretir. Ayrı bir kod
/// yolu yoktur: gerçek kullanıcıyla aynı hold ve checkout servislerini kullanır.
/// </summary>
public sealed class DemoCrowdProcessor(
    AppDbContext db, IClock clock, ITransactionRunner transactions, HoldService holds, OrderService orders, IOptions<DemoOptions> demo,
    IOptions<DemoCrowdOptions> options, DemoCrowdSchedule schedule, WorkerSignal<OutboxProcessor> outboxSignal, ILogger<DemoCrowdProcessor> logger) : IBatchProcessor
{
    public const string BotEmailPrefix = "demo-seyirci-";

    public string Name => "demo-crowd";

    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        if (!demo.Value.SimulatorEnabled || !options.Value.Enabled) return 0;
        var now = await clock.NowAsync(ct);
        if (!schedule.TryStart(now, TimeSpan.FromSeconds(options.Value.IntervalSeconds))) return 0;
        await SimulateOnePurchaseAsync(ct);
        return 0;
    }

    /// <returns>Sentetik alım yapıldıysa sipariş kimliği.</returns>
    public async Task<Guid?> SimulateOnePurchaseAsync(CancellationToken ct)
    {
        var now = await clock.NowAsync(ct);
        var sessions = await PublicCatalogEndpoints.PublicSessions(db, now)
            .Where(s => s.DemoSimulatorEnabled && s.SalesStartAt <= now && s.SalesEndAt > now)
            .Select(s => new { s.Id, s.SalesMode }).ToListAsync(ct);
        if (sessions.Count == 0) return null;
        var session = sessions[Random.Shared.Next(sessions.Count)];

        var bots = await db.Users.AsNoTracking().Where(u => u.IsDemo && u.Email!.StartsWith(BotEmailPrefix))
            .Where(u => !db.Reservations.Any(r => r.UserId == u.Id && r.SessionId == session.Id && r.Status == ReservationStatus.Active && r.ExpiresAt > now))
            .Select(u => u.Id).ToListAsync(ct);
        if (bots.Count == 0) return null;
        var botId = bots[Random.Shared.Next(bots.Count)];

        CreateHoldRequest? request;
        if (session.SalesMode == SalesMode.ReservedSeating)
        {
            var seats = await db.SessionSeats.AsNoTracking().Where(s => s.SessionId == session.Id)
                .Select(s => new { s.Id, Free = s.Status == SeatStatus.Available || (s.Status == SeatStatus.Held && s.HeldUntil <= now) }).ToListAsync(ct);
            var free = seats.Where(s => s.Free).Select(s => s.Id).ToList();
            if (seats.Count == 0 || free.Count < seats.Count * options.Value.StopBelowAvailableRatio) return null;
            request = new CreateHoldRequest([.. free.OrderBy(_ => Random.Shared.Next()).Take(Random.Shared.Next(1, 3))], null);
        }
        else
        {
            var categories = await db.GaCategories.AsNoTracking().Where(c => c.SessionId == session.Id)
                .Select(c => new { c.Id, c.Capacity, Free = c.Capacity - c.HeldCount - c.SoldCount }).ToListAsync(ct);
            var open = categories.Where(c => c.Free >= 2 && c.Free >= c.Capacity * options.Value.StopBelowAvailableRatio).ToList();
            if (open.Count == 0) return null;
            request = new CreateHoldRequest(null, [new HoldItemRequest(open[Random.Shared.Next(open.Count)].Id, Random.Shared.Next(1, 3))]);
        }

        try
        {
            var orderId = await transactions.RunAsync(async token =>
            {
                var hold = (HoldDto)(await holds.CreateAsync(session.Id, request, token, botId)).Body;
                // Aynı transaction'da checkout: demo izleyici her zaman varsayılan (başarılı) yöntemi kullanır.
                var order = (OrderDto)(await orders.CheckoutAsync(hold.Id, new CheckoutRequest(PaymentMethods.Success), token, botId)).Body;
                return order.Id;
            }, ct);
            outboxSignal.Notify();
            return orderId;
        }
        catch (ApiException e)
        {
            // Gerçek kullanıcıyla yarış kaybedildi (koltuk alındı vb.): beklenen durum, sessizce geçilir.
            logger.LogDebug("Demo purchase skipped: {Code}", e.Code);
            return null;
        }
    }
}

public sealed class DemoCrowdSchedule
{
    private readonly Lock _gate = new();
    private DateTimeOffset _nextRun = DateTimeOffset.MinValue;

    public bool TryStart(DateTimeOffset now, TimeSpan interval)
    {
        lock (_gate)
        {
            if (now < _nextRun) return false;
            _nextRun = now.Add(interval);
            return true;
        }
    }
}

/// <summary>Demo izleyici hesapları: parolasızdır (giriş yapamazlar), yalnız sentetik alım için kullanılır.</summary>
internal sealed class DemoCrowdSeed(UserManager<AppUser> users) : IDemoSeedContributor
{
    public int Order => 20;

    public async Task SeedAsync(SeedContext context, CancellationToken ct)
    {
        for (var i = 1; i <= 4; i++)
        {
            var email = $"{DemoCrowdProcessor.BotEmailPrefix}{i}@loca.demo";
            if (await users.FindByEmailAsync(email) is not null) { context.Report.Existing++; continue; }
            var result = await users.CreateAsync(new AppUser
            {
                Id = Guid.CreateVersion7(), UserName = email, Email = email, DisplayName = $"Demo İzleyici {i}", IsDemo = true, CreatedAt = context.Now,
            });
            if (!result.Succeeded) throw new InvalidOperationException($"Could not create demo audience user: {string.Join("; ", result.Errors.Select(e => e.Code))}");
            context.Report.Created++;
        }
    }
}
