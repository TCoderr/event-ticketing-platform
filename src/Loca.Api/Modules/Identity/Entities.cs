using Loca.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Loca.Api.Modules.Identity;

public sealed class AppUser : IdentityUser<Guid>
{
    public string DisplayName { get; set; } = "";

    /// <summary>Yalnız seed/CLI ile atanır; hiçbir HTTP ucu bu alanı yazmaz.</summary>
    public bool IsPlatformAdmin { get; set; }

    public bool IsDemo { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? DisabledAt { get; set; }
}

/// <summary>Sunucu oturum kaydı. Çerez yalnız bu satırın kimliğini taşır; logout satırı iptal eder.</summary>
public sealed class UserSession
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public AppUser User { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? RevokeReason { get; set; }
}

public sealed class Organization
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Slug { get; set; } = "";
    public OrganizationStatus Status { get; set; }
    public bool IsDemo { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public List<OrganizationMember> Members { get; set; } = [];
}

public sealed class OrganizationMember
{
    public Guid OrganizationId { get; set; }
    public Organization Organization { get; set; } = null!;
    public Guid UserId { get; set; }
    public AppUser User { get; set; } = null!;
    public MemberRole Role { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

internal static class IdentityModel
{
    public const string Schema = "identity";

    public static void Configure(ModelBuilder b)
    {
        b.Entity<AppUser>(e =>
        {
            e.ToTable("users", Schema);
            e.Property(x => x.DisplayName).HasMaxLength(120);
        });
        b.Entity<IdentityUserClaim<Guid>>().ToTable("user_claims", Schema);
        b.Entity<IdentityUserLogin<Guid>>().ToTable("user_logins", Schema);
        b.Entity<IdentityUserToken<Guid>>().ToTable("user_tokens", Schema);

        b.Entity<UserSession>(e =>
        {
            e.ToTable("user_sessions", Schema);
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.RevokeReason).HasMaxLength(40);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.UserId).HasFilter("revoked_at IS NULL");
            e.HasIndex(x => x.ExpiresAt);
        });

        b.Entity<Organization>(e =>
        {
            e.ToTable("organizations", Schema, t =>
                t.HasCheckConstraint("ck_organizations_status", "status IN ('PendingApproval','Active','Suspended')"));
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Name).HasMaxLength(160);
            e.Property(x => x.Slug).HasMaxLength(80);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(24);
            e.HasIndex(x => x.Slug).IsUnique();
        });

        b.Entity<OrganizationMember>(e =>
        {
            e.ToTable("organization_members", Schema, t =>
                t.HasCheckConstraint("ck_organization_members_role", "role IN ('OrganizerAdmin','GateStaff')"));
            e.HasKey(x => new { x.OrganizationId, x.UserId });
            e.Property(x => x.Role).HasConversion<string>().HasMaxLength(24);
            e.HasOne(x => x.Organization).WithMany(o => o.Members).HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.UserId);
        });
    }
}
