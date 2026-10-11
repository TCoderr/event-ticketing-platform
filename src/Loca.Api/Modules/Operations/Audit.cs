using System.Text.Json;
using Loca.Api.Infrastructure.Auth;
using Loca.Api.Infrastructure.Http;
using Loca.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Loca.Api.Modules.Operations;

/// <summary>Yalnız ekleme yapılan denetim kaydı. Çalışma zamanı rolünün UPDATE/DELETE yetkisi yoktur.</summary>
public sealed class AuditEntry
{
    public long Id { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public Guid? ActorUserId { get; set; }

    /// <summary>User | Worker | Provider | System</summary>
    public string ActorKind { get; set; } = "System";

    public string Action { get; set; } = "";
    public string EntityType { get; set; } = "";
    public string EntityId { get; set; } = "";
    public Guid? OrganizationId { get; set; }

    /// <summary>Kişisel veri ve secret içermeyen küçük JSON.</summary>
    public string? Data { get; set; }

    public string? CorrelationId { get; set; }
}

internal static class OperationsModel
{
    public const string Schema = "ops";

    public static void ConfigureAudit(ModelBuilder b)
    {
        b.Entity<AuditEntry>(e =>
        {
            e.ToTable("audit_log", Schema);
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).UseIdentityAlwaysColumn();
            e.Property(x => x.ActorKind).HasMaxLength(16);
            e.Property(x => x.Action).HasMaxLength(80);
            e.Property(x => x.EntityType).HasMaxLength(40);
            e.Property(x => x.EntityId).HasMaxLength(64);
            e.Property(x => x.Data).HasColumnType("jsonb");
            e.Property(x => x.CorrelationId).HasMaxLength(64);
            e.HasIndex(x => new { x.EntityType, x.EntityId });
            e.HasIndex(x => x.OccurredAt);
        });
    }
}

public interface IAuditWriter
{
    /// <summary>Kaydı geçerli DbContext'e ekler; çağıranın transaction'ı ile birlikte commit olur.</summary>
    void Add(DateTimeOffset now, string action, string entityType, object entityId, Guid? organizationId = null,
        object? data = null, string actorKind = "User", Guid? actorUserId = null);
}

internal sealed class AuditWriter(AppDbContext db, ICurrentUser currentUser, ICorrelationContext correlation) : IAuditWriter
{
    public void Add(DateTimeOffset now, string action, string entityType, object entityId, Guid? organizationId = null,
        object? data = null, string actorKind = "User", Guid? actorUserId = null)
    {
        var actor = actorUserId ?? currentUser.UserId;
        db.AuditLog.Add(new AuditEntry
        {
            OccurredAt = now,
            ActorUserId = actor,
            ActorKind = actor is null && actorKind == "User" ? "System" : actorKind,
            Action = action,
            EntityType = entityType,
            EntityId = entityId is Guid g ? g.ToString("D") : entityId.ToString() ?? "",
            OrganizationId = organizationId,
            Data = data is null ? null : JsonSerializer.Serialize(data, JsonDefaults.Options),
            CorrelationId = correlation.CorrelationId,
        });
    }
}
