using Loca.Api.Infrastructure.Auth;
using Loca.Api.Infrastructure.Http;
using Loca.Api.Infrastructure.Persistence;
using Loca.Api.Infrastructure.RateLimiting;
using Loca.Api.Infrastructure.Time;
using Loca.Api.Modules.Operations;
using Loca.Domain;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Loca.Api.Modules.Identity;

public sealed record RegisterRequest(string Email, string Password, string DisplayName);
public sealed record LoginRequest(string Email, string Password);
public sealed record CsrfResponse(string Token);
public sealed record MembershipDto(Guid OrganizationId, string OrganizationName, OrganizationStatus OrganizationStatus, MemberRole Role);
public sealed record MeResponse(Guid Id, string Email, string DisplayName, bool IsPlatformAdmin, bool IsDemo,
    IReadOnlyList<MembershipDto> Memberships);
public sealed record LoginResponse(string CsrfToken, MeResponse User);
public sealed record RegisterResponse(Guid Id);

internal static class AuthEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth").WithTags("Auth");

        // CSRF token'ı üreten tek uç. Antiforgery, çerez yazdığı yanıtı önbelleğe kapatır;
        // bu yüzden token üretimi katalog GET'lerine yayılmaz.
        group.MapGet("/csrf", (HttpContext http, IAntiforgery antiforgery) =>
        {
            var tokens = antiforgery.GetAndStoreTokens(http);
            http.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new CsrfResponse(tokens.RequestToken!));
        }).AllowAnonymous();

        group.MapPost("/register", RegisterAsync).AllowAnonymous().RequireRateLimiting(RateLimitPolicies.Register);
        group.MapPost("/login", LoginAsync).AllowAnonymous().RequireRateLimiting(RateLimitPolicies.Login);

        group.MapPost("/logout", async (HttpContext http, ISessionService sessions, CancellationToken ct) =>
        {
            await sessions.SignOutAsync(http, ct);
            return Results.NoContent();
        }).RequireAuthorization();

        group.MapPost("/logout-all", async (HttpContext http, ISessionService sessions, ICurrentUser user, CancellationToken ct) =>
        {
            await sessions.RevokeAllAsync(user.RequireUserId(), "LogoutAll", ct);
            await sessions.SignOutAsync(http, ct);
            return Results.NoContent();
        }).RequireAuthorization();

        group.MapGet("/me", async (HttpContext http, ICurrentUser user, AppDbContext db, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            return Results.Ok(await LoadMeAsync(db, user.RequireUserId(), ct));
        }).RequireAuthorization();
    }

    private static async Task<IResult> RegisterAsync(
        RegisterRequest request, UserManager<AppUser> users, AppDbContext db, IClock clock, IAuditWriter audit, CancellationToken ct)
    {
        var email = (request.Email ?? "").Trim();
        new Validator()
            .Check(email.Length is >= 5 and <= 254 && email.Contains('@') && !email.Any(char.IsWhiteSpace), "email", "A valid e-mail address is required.")
            .Required(request.DisplayName, "displayName", 120, 2)
            .Check(request.Password is { Length: <= 128 }, "password", "Password is required and must be at most 128 characters.")
            .ThrowIfInvalid();

        var now = await clock.NowAsync(ct);
        var user = new AppUser
        {
            Id = Guid.CreateVersion7(),
            UserName = email,
            Email = email,
            DisplayName = request.DisplayName.Trim(),
            CreatedAt = now,
            // İstemci bu alanları gönderemez; her zaman sunucu varsayılanı.
            IsPlatformAdmin = false,
            IsDemo = false,
        };

        var result = await users.CreateAsync(user, request.Password);
        if (!result.Succeeded)
        {
            if (result.Errors.Any(e => e.Code is "DuplicateUserName" or "DuplicateEmail"))
                throw ApiErrors.Conflict(ErrorCodes.EmailAlreadyRegistered, "An account with this e-mail already exists.");
            throw ApiErrors.BadRequest(ErrorCodes.WeakPassword, "The password does not meet the policy.",
                new Dictionary<string, object?> { ["errors"] = new Dictionary<string, string[]> { ["password"] = [.. result.Errors.Select(e => e.Description)] } });
        }

        audit.Add(now, "user.registered", "user", user.Id, actorUserId: user.Id);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/auth/me", new RegisterResponse(user.Id));
    }

    private static async Task<IResult> LoginAsync(
        LoginRequest request, HttpContext http, UserManager<AppUser> users, ISessionService sessions,
        AppDbContext db, IClock clock, IAuditWriter audit, CancellationToken ct)
    {
        var email = (request.Email ?? "").Trim();
        var password = request.Password ?? "";
        if (email.Length is 0 or > 254 || password.Length is 0 or > 128) throw InvalidCredentials();

        var user = await users.FindByEmailAsync(email);
        if (user is null)
        {
            // Var olmayan hesap ile yanlış parola arasındaki süre farkını azaltmak için özet yine hesaplanır.
            users.PasswordHasher.HashPassword(new AppUser(), password);
            throw InvalidCredentials();
        }

        if (user.DisabledAt is not null || await users.IsLockedOutAsync(user)) throw InvalidCredentials();

        if (!await users.CheckPasswordAsync(user, password))
        {
            await users.AccessFailedAsync(user);
            throw InvalidCredentials();
        }
        await users.ResetAccessFailedCountAsync(user);

        var csrfToken = await sessions.SignInAsync(http, user, ct);
        audit.Add(await clock.NowAsync(ct), "user.logged_in", "user", user.Id, actorUserId: user.Id);
        await db.SaveChangesAsync(ct);

        http.Response.Headers.CacheControl = "no-store";
        return Results.Ok(new LoginResponse(csrfToken, await LoadMeAsync(db, user.Id, ct)));
    }

    // Hesap var/yok, kilitli, devre dışı ayrımı istemciye verilmez.
    private static ApiException InvalidCredentials() =>
        new(StatusCodes.Status401Unauthorized, ErrorCodes.InvalidCredentials, "The e-mail or password is incorrect.");

    internal static async Task<MeResponse> LoadMeAsync(AppDbContext db, Guid userId, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().Where(u => u.Id == userId)
            .Select(u => new { u.Id, u.Email, u.DisplayName, u.IsPlatformAdmin, u.IsDemo })
            .FirstOrDefaultAsync(ct) ?? throw ApiErrors.Unauthenticated();

        var memberships = await db.OrganizationMembers.AsNoTracking()
            .Where(m => m.UserId == userId)
            .OrderBy(m => m.Organization.Name).ThenBy(m => m.OrganizationId)
            .Select(m => new MembershipDto(m.OrganizationId, m.Organization.Name, m.Organization.Status, m.Role))
            .ToListAsync(ct);

        return new MeResponse(user.Id, user.Email ?? "", user.DisplayName, user.IsPlatformAdmin, user.IsDemo, memberships);
    }
}
