using System.Security.Cryptography;
using System.Text;
using Loca.Api.Infrastructure.Auth;
using Loca.Api.Infrastructure.Http;
using Loca.Api.Infrastructure.Persistence;
using Loca.Api.Infrastructure.Time;
using Loca.Api.Modules.Operations;
using Loca.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Loca.Api.Modules.Identity;

public sealed record CreateOrganizationRequest(string Name);
public sealed record UpdateOrganizationRequest(string Name);
public sealed record OrganizationDto(Guid Id, string Name, string Slug, OrganizationStatus Status, bool IsDemo, MemberRole? MyRole);
public sealed record AddMemberRequest(string Email, MemberRole Role);
public sealed record MemberDto(Guid UserId, string DisplayName, string Email, MemberRole Role);
public sealed record AdminOrganizationDto(Guid Id, string Name, string Slug, OrganizationStatus Status, bool IsDemo, DateTimeOffset CreatedAt, int MemberCount);

internal static class OrganizationEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/organizations", CreateAsync).RequireAuthorization().WithTags("Organizations");

        var organizer = app.MapGroup("/api/organizer/organizations").RequireAuthorization().WithTags("Organizer");
        organizer.MapGet("/", ListMineAsync);
        organizer.MapGet("/{organizationId:guid}", GetAsync);
        organizer.MapPut("/{organizationId:guid}", UpdateAsync);
        organizer.MapGet("/{organizationId:guid}/members", ListMembersAsync);
        organizer.MapPost("/{organizationId:guid}/members", AddMemberAsync);
        organizer.MapDelete("/{organizationId:guid}/members/{userId:guid}", RemoveMemberAsync);

        var admin = app.MapGroup("/api/admin/organizations").RequireAuthorization().WithTags("Admin");
        admin.MapGet("/", AdminListAsync);
        admin.MapPost("/{organizationId:guid}/approve", (Guid organizationId, AppDbContext db, ICurrentUser user, IClock clock, IAuditWriter audit, CancellationToken ct) =>
            AdminTransitionAsync(organizationId, OrganizationStatus.Active, "organization.approved", OrganizationStatus.PendingApproval, db, user, clock, audit, ct));
        admin.MapPost("/{organizationId:guid}/suspend", (Guid organizationId, AppDbContext db, ICurrentUser user, IClock clock, IAuditWriter audit, CancellationToken ct) =>
            AdminTransitionAsync(organizationId, OrganizationStatus.Suspended, "organization.suspended", OrganizationStatus.Active, db, user, clock, audit, ct));
        admin.MapPost("/{organizationId:guid}/reinstate", (Guid organizationId, AppDbContext db, ICurrentUser user, IClock clock, IAuditWriter audit, CancellationToken ct) =>
            AdminTransitionAsync(organizationId, OrganizationStatus.Active, "organization.reinstated", OrganizationStatus.Suspended, db, user, clock, audit, ct));
    }

    private static async Task<IResult> CreateAsync(
        CreateOrganizationRequest request, AppDbContext db, ICurrentUser user, IClock clock, IAuditWriter audit, CancellationToken ct)
    {
        var userId = user.RequireUserId();
        new Validator().Required(request.Name, "name", 160, 3).ThrowIfInvalid();

        var now = await clock.NowAsync(ct);
        var organization = new Organization
        {
            Id = Guid.CreateVersion7(),
            Name = request.Name.Trim(),
            Slug = Slugs.Create(request.Name),
            Status = OrganizationStatus.PendingApproval,
            CreatedAt = now,
        };
        organization.Members.Add(new OrganizationMember { UserId = userId, Role = MemberRole.OrganizerAdmin, CreatedAt = now });
        db.Organizations.Add(organization);
        audit.Add(now, "organization.applied", "organization", organization.Id, organization.Id);
        await db.SaveChangesAsync(ct);

        return Results.Created($"/api/organizer/organizations/{organization.Id}",
            new OrganizationDto(organization.Id, organization.Name, organization.Slug, organization.Status, organization.IsDemo, MemberRole.OrganizerAdmin));
    }

    private static async Task<IResult> ListMineAsync(AppDbContext db, ICurrentUser user, CancellationToken ct)
    {
        var userId = user.RequireUserId();
        var items = await db.OrganizationMembers.AsNoTracking()
            .Where(m => m.UserId == userId)
            .OrderBy(m => m.Organization.Name).ThenBy(m => m.OrganizationId)
            .Select(m => new OrganizationDto(m.Organization.Id, m.Organization.Name, m.Organization.Slug, m.Organization.Status, m.Organization.IsDemo, m.Role))
            .ToListAsync(ct);
        return Results.Ok(items);
    }

    private static async Task<IResult> GetAsync(Guid organizationId, AppDbContext db, IOrganizationAccess access, CancellationToken ct)
    {
        var role = await access.GetRoleAsync(organizationId, ct) ?? throw ApiErrors.NotFound();
        var dto = await db.Organizations.AsNoTracking().Where(o => o.Id == organizationId)
            .Select(o => new OrganizationDto(o.Id, o.Name, o.Slug, o.Status, o.IsDemo, role))
            .FirstOrDefaultAsync(ct) ?? throw ApiErrors.NotFound();
        return Results.Ok(dto);
    }

    private static async Task<IResult> UpdateAsync(
        Guid organizationId, UpdateOrganizationRequest request, AppDbContext db, IOrganizationAccess access,
        IClock clock, IAuditWriter audit, CancellationToken ct)
    {
        await access.RequireAdminAsync(organizationId, ct);
        new Validator().Required(request.Name, "name", 160, 3).ThrowIfInvalid();

        var organization = await db.Organizations.FirstOrDefaultAsync(o => o.Id == organizationId, ct) ?? throw ApiErrors.NotFound();
        organization.Name = request.Name.Trim();
        audit.Add(await clock.NowAsync(ct), "organization.updated", "organization", organization.Id, organization.Id);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new OrganizationDto(organization.Id, organization.Name, organization.Slug, organization.Status, organization.IsDemo, MemberRole.OrganizerAdmin));
    }

    private static async Task<IResult> ListMembersAsync(Guid organizationId, AppDbContext db, IOrganizationAccess access, CancellationToken ct)
    {
        await access.RequireAdminAsync(organizationId, ct);
        var members = await db.OrganizationMembers.AsNoTracking()
            .Where(m => m.OrganizationId == organizationId)
            .OrderBy(m => m.User.DisplayName).ThenBy(m => m.UserId)
            .Select(m => new MemberDto(m.UserId, m.User.DisplayName, m.User.Email ?? "", m.Role))
            .ToListAsync(ct);
        return Results.Ok(members);
    }

    private static async Task<IResult> AddMemberAsync(
        Guid organizationId, AddMemberRequest request, AppDbContext db, IOrganizationAccess access,
        UserManager<AppUser> users, IClock clock, IAuditWriter audit, CancellationToken ct)
    {
        await access.RequireAdminAsync(organizationId, ct);
        new Validator()
            .Required(request.Email, "email", 254, 5)
            .Check(Enum.IsDefined(request.Role), "role", "Role must be OrganizerAdmin or GateStaff.")
            .ThrowIfInvalid();

        var target = await users.FindByEmailAsync(request.Email.Trim());
        if (target is null || target.DisabledAt is not null)
            throw ApiErrors.Unprocessable(ErrorCodes.UserNotFound, "No registered user has this e-mail address.");

        var now = await clock.NowAsync(ct);
        var member = await db.OrganizationMembers.FirstOrDefaultAsync(m => m.OrganizationId == organizationId && m.UserId == target.Id, ct);
        if (member is null)
        {
            member = new OrganizationMember { OrganizationId = organizationId, UserId = target.Id, Role = request.Role, CreatedAt = now };
            db.OrganizationMembers.Add(member);
        }
        else
        {
            if (member.Role == MemberRole.OrganizerAdmin && request.Role != MemberRole.OrganizerAdmin)
                await EnsureAnotherAdminAsync(db, organizationId, target.Id, ct);
            member.Role = request.Role;
        }

        audit.Add(now, "organization.member_set", "organization", organizationId, organizationId,
            new { memberUserId = target.Id, role = request.Role.ToString() });
        await db.SaveChangesAsync(ct);
        return Results.Ok(new MemberDto(target.Id, target.DisplayName, target.Email ?? "", member.Role));
    }

    private static async Task<IResult> RemoveMemberAsync(
        Guid organizationId, Guid userId, AppDbContext db, IOrganizationAccess access, IClock clock, IAuditWriter audit, CancellationToken ct)
    {
        await access.RequireAdminAsync(organizationId, ct);
        var member = await db.OrganizationMembers.FirstOrDefaultAsync(m => m.OrganizationId == organizationId && m.UserId == userId, ct)
            ?? throw ApiErrors.NotFound();
        if (member.Role == MemberRole.OrganizerAdmin)
            await EnsureAnotherAdminAsync(db, organizationId, userId, ct);

        db.OrganizationMembers.Remove(member);
        audit.Add(await clock.NowAsync(ct), "organization.member_removed", "organization", organizationId, organizationId, new { memberUserId = userId });
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task EnsureAnotherAdminAsync(AppDbContext db, Guid organizationId, Guid exceptUserId, CancellationToken ct)
    {
        var another = await db.OrganizationMembers.AnyAsync(
            m => m.OrganizationId == organizationId && m.UserId != exceptUserId && m.Role == MemberRole.OrganizerAdmin, ct);
        if (!another)
            throw ApiErrors.Conflict(ErrorCodes.LastAdminCannotLeave, "An organization must keep at least one OrganizerAdmin.");
    }

    private static async Task<IResult> AdminListAsync(AppDbContext db, ICurrentUser user, OrganizationStatus? status, CancellationToken ct)
    {
        user.RequirePlatformAdmin();
        var query = db.Organizations.AsNoTracking();
        if (status is not null) query = query.Where(o => o.Status == status);
        var items = await query.OrderBy(o => o.CreatedAt).ThenBy(o => o.Id)
            .Select(o => new AdminOrganizationDto(o.Id, o.Name, o.Slug, o.Status, o.IsDemo, o.CreatedAt, o.Members.Count))
            .ToListAsync(ct);
        return Results.Ok(items);
    }

    private static async Task<IResult> AdminTransitionAsync(
        Guid organizationId, OrganizationStatus target, string action, OrganizationStatus expectedFrom,
        AppDbContext db, ICurrentUser user, IClock clock, IAuditWriter audit, CancellationToken ct)
    {
        user.RequirePlatformAdmin();
        var organization = await db.Organizations.FirstOrDefaultAsync(o => o.Id == organizationId, ct) ?? throw ApiErrors.NotFound();
        if (organization.Status != expectedFrom || !StateMachines.CanTransition(organization.Status, target))
            throw ApiErrors.Conflict(ErrorCodes.InvalidStateTransition, $"Organization is {organization.Status}; expected {expectedFrom}.");

        // Koşullu güncelleme: eşzamanlı iki yönetici işleminden yalnız biri etkili olur.
        var now = await clock.NowAsync(ct);
        var changed = await db.Organizations
            .Where(o => o.Id == organizationId && o.Status == expectedFrom)
            .ExecuteUpdateAsync(u => u.SetProperty(o => o.Status, target), ct);
        if (changed != 1)
            throw ApiErrors.Conflict(ErrorCodes.InvalidStateTransition, "Organization status changed concurrently.");

        audit.Add(now, action, "organization", organizationId, organizationId);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { id = organizationId, status = target });
    }
}

public static class Slugs
{
    private static readonly Dictionary<char, char> TurkishMap = new()
    {
        ['ç'] = 'c', ['ğ'] = 'g', ['ı'] = 'i', ['ö'] = 'o', ['ş'] = 's', ['ü'] = 'u',
        ['Ç'] = 'c', ['Ğ'] = 'g', ['İ'] = 'i', ['I'] = 'i', ['Ö'] = 'o', ['Ş'] = 's', ['Ü'] = 'u',
    };

    public static string Create(string text, bool withSuffix = true)
    {
        var sb = new StringBuilder(text.Length + 8);
        var lastDash = true;
        foreach (var raw in text.Trim())
        {
            var c = TurkishMap.TryGetValue(raw, out var mapped) ? mapped : char.ToLowerInvariant(raw);
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                sb.Append(c);
                lastDash = false;
            }
            else if (!lastDash)
            {
                sb.Append('-');
                lastDash = true;
            }
            if (sb.Length >= 60) break;
        }
        var slug = sb.ToString().Trim('-');
        if (slug.Length == 0) slug = "x";
        return withSuffix ? $"{slug}-{RandomNumberGenerator.GetHexString(6, lowercase: true)}" : slug;
    }
}
