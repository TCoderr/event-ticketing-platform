using System.Security.Claims;
using Loca.Api.Infrastructure.Http;
using Loca.Api.Infrastructure.Persistence;
using Loca.Api.Infrastructure.Time;
using Loca.Api.Modules.Identity;
using Loca.Domain;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;

namespace Loca.Api.Infrastructure.Auth;

public sealed class AuthSessionOptions
{
    public const string Section = "Session";
    public int AbsoluteLifetimeMinutes { get; set; } = 12 * 60;
    public int IdleTimeoutMinutes { get; set; } = 120;
    public int LastSeenUpdateSeconds { get; set; } = 300;
}

public static class AuthConstants
{
    public const string Scheme = "LocaSession";
    public const string CookieName = "__Host-loca-session";
    public const string CsrfCookieName = "__Host-loca-csrf";
    public const string CsrfHeaderName = "X-CSRF-TOKEN";
    public const string SessionIdClaim = "sid";
    public const string StampClaim = "stamp";
}

/// <summary>İstek başına doğrulanmış kimlik. Değerler çerezden değil, bu istekte veritabanından okunan oturumdan gelir.</summary>
public interface ICurrentUser
{
    bool IsAuthenticated { get; }
    Guid? UserId { get; }
    Guid? SessionId { get; }
    bool IsPlatformAdmin { get; }
    Guid RequireUserId();
    void RequirePlatformAdmin();
}

internal sealed record ValidatedSession(Guid UserId, Guid SessionId, bool IsPlatformAdmin);

internal sealed class HttpCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    internal const string ItemKey = "loca.validated_session";

    private ValidatedSession? Session =>
        accessor.HttpContext?.Items.TryGetValue(ItemKey, out var value) == true ? value as ValidatedSession : null;

    public bool IsAuthenticated => Session is not null;
    public Guid? UserId => Session?.UserId;
    public Guid? SessionId => Session?.SessionId;
    public bool IsPlatformAdmin => Session?.IsPlatformAdmin ?? false;

    public Guid RequireUserId() => Session?.UserId ?? throw ApiErrors.Unauthenticated();

    public void RequirePlatformAdmin()
    {
        if (Session is null) throw ApiErrors.Unauthenticated();
        if (!Session.IsPlatformAdmin) throw ApiErrors.Forbidden();
    }
}

/// <summary>
/// Çerez olayları: her istekte oturum satırını doğrular (iptal, süre, boşta kalma, devre dışı kullanıcı,
/// security stamp). Yönlendirme yerine JSON ProblemDetails döner.
/// </summary>
internal sealed class SessionCookieEvents(
    AppDbContext db, IClock clock, Microsoft.Extensions.Options.IOptions<AuthSessionOptions> options) : CookieAuthenticationEvents
{
    public override async Task ValidatePrincipal(CookieValidatePrincipalContext context)
    {
        var principal = context.Principal;
        if (!Guid.TryParse(principal?.FindFirstValue(ClaimTypes.NameIdentifier), out var userId) ||
            !Guid.TryParse(principal?.FindFirstValue(AuthConstants.SessionIdClaim), out var sessionId))
        {
            context.RejectPrincipal();
            return;
        }

        var ct = context.HttpContext.RequestAborted;
        var row = await db.UserSessions.AsNoTracking()
            .Where(s => s.Id == sessionId && s.UserId == userId)
            .Select(s => new
            {
                s.RevokedAt, s.ExpiresAt, s.LastSeenAt,
                s.User.IsPlatformAdmin, s.User.DisabledAt, s.User.SecurityStamp,
            })
            .FirstOrDefaultAsync(ct);

        if (row is null || row.RevokedAt is not null || row.DisabledAt is not null ||
            !string.Equals(row.SecurityStamp, principal!.FindFirstValue(AuthConstants.StampClaim), StringComparison.Ordinal))
        {
            context.RejectPrincipal();
            return;
        }

        var now = await clock.NowAsync(ct);
        var opts = options.Value;
        if (now >= row.ExpiresAt || now - row.LastSeenAt >= TimeSpan.FromMinutes(opts.IdleTimeoutMinutes))
        {
            await db.UserSessions.Where(s => s.Id == sessionId && s.RevokedAt == null)
                .ExecuteUpdateAsync(u => u.SetProperty(s => s.RevokedAt, now).SetProperty(s => s.RevokeReason, "Expired"), ct);
            context.RejectPrincipal();
            return;
        }

        if (now - row.LastSeenAt >= TimeSpan.FromSeconds(opts.LastSeenUpdateSeconds))
        {
            await db.UserSessions.Where(s => s.Id == sessionId)
                .ExecuteUpdateAsync(u => u.SetProperty(s => s.LastSeenAt, now), ct);
        }

        context.HttpContext.Items[HttpCurrentUser.ItemKey] = new ValidatedSession(userId, sessionId, row.IsPlatformAdmin);
    }

    public override Task RedirectToLogin(RedirectContext<CookieAuthenticationOptions> context) =>
        ProblemWriter.WriteAsync(context.HttpContext, StatusCodes.Status401Unauthorized, ErrorCodes.Unauthenticated,
            "Authentication is required.");

    public override Task RedirectToAccessDenied(RedirectContext<CookieAuthenticationOptions> context) =>
        ProblemWriter.WriteAsync(context.HttpContext, StatusCodes.Status403Forbidden, ErrorCodes.Forbidden,
            "You do not have permission for this operation.");
}

public interface ISessionService
{
    Task<string> SignInAsync(HttpContext http, AppUser user, CancellationToken ct);
    Task SignOutAsync(HttpContext http, CancellationToken ct);
    Task<int> RevokeAllAsync(Guid userId, string reason, CancellationToken ct);
}

internal sealed class SessionService(
    AppDbContext db, IClock clock, IAntiforgery antiforgery, ICurrentUser currentUser,
    Microsoft.Extensions.Options.IOptions<AuthSessionOptions> options) : ISessionService
{
    /// <returns>Yeni kimliğe bağlı CSRF istek token'ı.</returns>
    public async Task<string> SignInAsync(HttpContext http, AppUser user, CancellationToken ct)
    {
        var now = await clock.NowAsync(ct);
        var session = new UserSession
        {
            Id = Guid.CreateVersion7(),
            UserId = user.Id,
            CreatedAt = now,
            LastSeenAt = now,
            ExpiresAt = now.AddMinutes(options.Value.AbsoluteLifetimeMinutes),
        };
        db.UserSessions.Add(session);
        await db.SaveChangesAsync(ct);

        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString("D")),
            new Claim(AuthConstants.SessionIdClaim, session.Id.ToString("D")),
            new Claim(AuthConstants.StampClaim, user.SecurityStamp ?? ""),
        ], AuthConstants.Scheme);
        var principal = new ClaimsPrincipal(identity);

        await http.SignInAsync(AuthConstants.Scheme, principal, new AuthenticationProperties
        {
            IsPersistent = false,
            IssuedUtc = now,
            ExpiresUtc = session.ExpiresAt,
            AllowRefresh = false,
        });

        // Antiforgery token'ı kimliğe bağlıdır; yeni kimlik için bu yanıtta yeniden üretilir.
        http.User = principal;
        return antiforgery.GetAndStoreTokens(http).RequestToken!;
    }

    public async Task SignOutAsync(HttpContext http, CancellationToken ct)
    {
        if (currentUser.SessionId is { } sessionId)
        {
            var now = await clock.NowAsync(ct);
            await db.UserSessions.Where(s => s.Id == sessionId && s.RevokedAt == null)
                .ExecuteUpdateAsync(u => u.SetProperty(s => s.RevokedAt, now).SetProperty(s => s.RevokeReason, "Logout"), ct);
        }
        await http.SignOutAsync(AuthConstants.Scheme);
    }

    public async Task<int> RevokeAllAsync(Guid userId, string reason, CancellationToken ct)
    {
        var now = await clock.NowAsync(ct);
        return await db.UserSessions.Where(s => s.UserId == userId && s.RevokedAt == null)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.RevokedAt, now).SetProperty(s => s.RevokeReason, reason), ct);
    }
}

/// <summary>
/// Güvensiz metotlarda CSRF token'ı doğrular. Webhook ve simülatör uçları çerez kullanmaz;
/// kendi kimlik doğrulamaları (HMAC / API anahtarı) vardır ve kapsam dışıdır.
/// </summary>
internal sealed class CsrfMiddleware(RequestDelegate next, IAntiforgery antiforgery)
{
    public async Task InvokeAsync(HttpContext http)
    {
        var method = http.Request.Method;
        var unsafeMethod = !(HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method) || HttpMethods.IsTrace(method));
        if (unsafeMethod && RequiresCsrf(http.Request.Path))
        {
            await antiforgery.ValidateRequestAsync(http); // AntiforgeryValidationException → 403 csrf_invalid
        }
        await next(http);
    }

    private static bool RequiresCsrf(PathString path) =>
        path.StartsWithSegments("/api") && !path.StartsWithSegments("/api/webhooks");
}

/// <summary>Organizasyon üyeliğini her çağrıda veritabanından doğrular; istemciden gelen kimlik yetki kanıtı değildir.</summary>
public interface IOrganizationAccess
{
    /// <summary>Üye değilse 404 (varlık sızdırılmaz), üye fakat rolü yetersizse 403.</summary>
    Task RequireAdminAsync(Guid organizationId, CancellationToken ct);
    Task<MemberRole?> GetRoleAsync(Guid organizationId, CancellationToken ct);
}

internal sealed class OrganizationAccess(AppDbContext db, ICurrentUser currentUser) : IOrganizationAccess
{
    public async Task<MemberRole?> GetRoleAsync(Guid organizationId, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        var role = await db.OrganizationMembers.AsNoTracking()
            .Where(m => m.OrganizationId == organizationId && m.UserId == userId)
            .Select(m => (MemberRole?)m.Role)
            .FirstOrDefaultAsync(ct);
        return role;
    }

    public async Task RequireAdminAsync(Guid organizationId, CancellationToken ct)
    {
        var role = await GetRoleAsync(organizationId, ct);
        if (role is null) throw ApiErrors.NotFound();
        if (role != MemberRole.OrganizerAdmin) throw ApiErrors.Forbidden();
    }
}
