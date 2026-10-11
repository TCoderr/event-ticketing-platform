using Loca.Api.Infrastructure.Config;
using Loca.Api.Infrastructure.Persistence;
using Loca.Api.Infrastructure.Time;
using Loca.Api.Modules.Identity;
using Loca.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Loca.Api.Modules.Operations;

/// <summary>
/// Sentetik demo verisi. Yalnız <c>Demo:SeedEnabled=true</c> iken ve yalnız <c>seed-demo</c> kipinden çalışır.
/// İdempotent: var olan kayıtlar değiştirilmez. Parolalar depoda değil, kullanıcı adıyla eşleşen
/// özel dosyalardadır: <c>&lt;e-posta&gt;.password.txt</c>.
/// </summary>
public sealed class DemoSeeder(
    AppDbContext db, UserManager<AppUser> users, IClock clock, IOptions<DemoOptions> options,
    IEnumerable<IDemoSeedContributor> contributors, ILogger<DemoSeeder> logger)
{
    public sealed record DemoUser(string Email, string DisplayName, bool PlatformAdmin = false);

    public static readonly DemoUser PlatformAdmin = new("platform-admin@loca.demo", "Demo Platform Yöneticisi", PlatformAdmin: true);
    public static readonly DemoUser OrganizerKumru = new("organizer-kumru@loca.demo", "Demo Organizatör (Kumru Sahne)");
    public static readonly DemoUser OrganizerMarti = new("organizer-marti@loca.demo", "Demo Organizatör (Martı Etkinlik)");
    public static readonly DemoUser GateKumru = new("gate-kumru@loca.demo", "Demo Kapı Görevlisi (Kumru Sahne)");
    public static readonly DemoUser CustomerAyse = new("customer-ayse@loca.demo", "Demo Müşteri Ayşe");
    public static readonly DemoUser CustomerMehmet = new("customer-mehmet@loca.demo", "Demo Müşteri Mehmet");

    public static readonly IReadOnlyList<DemoUser> Users =
        [PlatformAdmin, OrganizerKumru, OrganizerMarti, GateKumru, CustomerAyse, CustomerMehmet];

    public const string KumruSlug = "demo-kumru-sahne";
    public const string MartiSlug = "demo-marti-etkinlik";

    public static string PasswordFileName(string email) => $"{email}.password.txt";

    public async Task<SeedReport> RunAsync(CancellationToken ct)
    {
        var demo = options.Value;
        if (!demo.SeedEnabled)
            throw new InvalidOperationException("Demo seeding is disabled. Set Demo:SeedEnabled=true to seed synthetic data.");

        var report = new SeedReport();
        var now = await clock.NowAsync(ct);

        var ids = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var demoUser in Users)
            ids[demoUser.Email] = await EnsureUserAsync(demoUser, demo.SeedPasswordDirectory, now, report);

        var kumru = await EnsureOrganizationAsync(KumruSlug, "Kumru Sahne (Demo)", now, report, ct);
        var marti = await EnsureOrganizationAsync(MartiSlug, "Martı Etkinlik (Demo)", now, report, ct);
        await EnsureMemberAsync(kumru, ids[OrganizerKumru.Email], MemberRole.OrganizerAdmin, now, report, ct);
        await EnsureMemberAsync(kumru, ids[GateKumru.Email], MemberRole.GateStaff, now, report, ct);
        await EnsureMemberAsync(marti, ids[OrganizerMarti.Email], MemberRole.OrganizerAdmin, now, report, ct);
        await db.SaveChangesAsync(ct);

        var context = new SeedContext(db, now, ids, kumru, marti, report);
        foreach (var contributor in contributors.OrderBy(c => c.Order))
            await contributor.SeedAsync(context, ct);

        logger.LogInformation("Demo seed finished: {Created} created, {Existing} already present.", report.Created, report.Existing);
        return report;
    }

    private async Task<Guid> EnsureUserAsync(DemoUser demoUser, string? passwordDirectory, DateTimeOffset now, SeedReport report)
    {
        var existing = await users.FindByEmailAsync(demoUser.Email);
        if (existing is not null)
        {
            report.Existing++;
            return existing.Id;
        }

        var password = SecretFile.ReadRequired(
            passwordDirectory is null ? null : Path.Combine(passwordDirectory, PasswordFileName(demoUser.Email)),
            $"Demo:SeedPasswordDirectory/{PasswordFileName(demoUser.Email)}");

        var user = new AppUser
        {
            Id = Guid.CreateVersion7(),
            UserName = demoUser.Email,
            Email = demoUser.Email,
            EmailConfirmed = true,
            DisplayName = demoUser.DisplayName,
            IsPlatformAdmin = demoUser.PlatformAdmin,
            IsDemo = true,
            CreatedAt = now,
        };
        var result = await users.CreateAsync(user, password);
        if (!result.Succeeded)
            throw new InvalidOperationException($"Could not create demo user {demoUser.Email}: {string.Join("; ", result.Errors.Select(e => e.Code))}");
        report.Created++;
        return user.Id;
    }

    private async Task<Guid> EnsureOrganizationAsync(string slug, string name, DateTimeOffset now, SeedReport report, CancellationToken ct)
    {
        var existing = await db.Organizations.Where(o => o.Slug == slug).Select(o => (Guid?)o.Id).FirstOrDefaultAsync(ct);
        if (existing is not null)
        {
            report.Existing++;
            return existing.Value;
        }
        var organization = new Organization
        {
            Id = Guid.CreateVersion7(), Name = name, Slug = slug, Status = OrganizationStatus.Active, IsDemo = true, CreatedAt = now,
        };
        db.Organizations.Add(organization);
        report.Created++;
        return organization.Id;
    }

    private async Task EnsureMemberAsync(Guid organizationId, Guid userId, MemberRole role, DateTimeOffset now, SeedReport report, CancellationToken ct)
    {
        var exists = await db.OrganizationMembers.AnyAsync(m => m.OrganizationId == organizationId && m.UserId == userId, ct) ||
                     db.OrganizationMembers.Local.Any(m => m.OrganizationId == organizationId && m.UserId == userId);
        if (exists)
        {
            report.Existing++;
            return;
        }
        db.OrganizationMembers.Add(new OrganizationMember { OrganizationId = organizationId, UserId = userId, Role = role, CreatedAt = now });
        report.Created++;
    }
}

public sealed class SeedReport
{
    public int Created { get; set; }
    public int Existing { get; set; }
}

/// <summary>Modüllerin (katalog vb.) demo verisi katkısı. Her katkı idempotent olmalıdır.</summary>
public interface IDemoSeedContributor
{
    int Order { get; }
    Task SeedAsync(SeedContext context, CancellationToken ct);
}

public sealed record SeedContext(
    AppDbContext Db, DateTimeOffset Now, IReadOnlyDictionary<string, Guid> UserIds, Guid KumruOrganizationId, Guid MartiOrganizationId, SeedReport Report);
