using Loca.Api.Modules.Operations;
using Microsoft.EntityFrameworkCore;

namespace Loca.Api.Modules.Tickets;

/// <summary>Demo kapı görevlisini, demo organizasyonunun seanslarına atar (idempotent). Yalnız seed-demo kipinde çalışır.</summary>
internal sealed class GateDemoSeed : IDemoSeedContributor
{
    public int Order => 30;

    public async Task SeedAsync(SeedContext context, CancellationToken ct)
    {
        var gateUserId = context.UserIds[DemoSeeder.GateKumru.Email];
        var organizerId = context.UserIds[DemoSeeder.OrganizerKumru.Email];
        var sessions = await context.Db.Sessions.AsNoTracking()
            .Where(s => s.OrganizationId == context.KumruOrganizationId && s.Event.IsDemo &&
                        !context.Db.GateAssignments.Any(a => a.SessionId == s.Id && a.UserId == gateUserId))
            .Select(s => s.Id).ToListAsync(ct);
        foreach (var sessionId in sessions)
        {
            context.Db.GateAssignments.Add(new GateAssignment { SessionId = sessionId, UserId = gateUserId, AssignedBy = organizerId, CreatedAt = context.Now });
            context.Report.Created++;
        }
        await context.Db.SaveChangesAsync(ct);
    }
}
