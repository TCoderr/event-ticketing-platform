using System.Text.RegularExpressions;
using Loca.Api.PspSimulator;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Loca.Api.Infrastructure.Persistence;

/// <summary>
/// Migration'ları şema sahibi rolüyle uygular ve çalışma zamanı rolünün yetkilerini eşitler.
/// Çalışma zamanı (<c>serve</c>) migration çalıştırmaz.
/// </summary>
public static partial class Migrator
{
    /// <summary>Çalışma zamanı rolünün yalnız INSERT/SELECT yapabildiği (değişmez / yalnız-ekleme) tablolar.</summary>
    public static readonly (string Schema, string Table)[] AppendOnlyTables =
    [
        ("ops", "audit_log"),
        ("catalog", "seat_layouts"),
        ("catalog", "layout_seats"),
        ("inventory", "reservation_lines"),
        ("tickets", "scan_log"),
    ];

    public static async Task RunAsync(string ownerConnectionString, string appRole, CancellationToken ct)
    {
        var builder = new DbContextOptionsBuilder<AppDbContext>();
        AppDbContext.Configure(builder, ownerConnectionString);
        await using var db = new AppDbContext(builder.Options);
        db.Database.SetCommandTimeout(120);
        await db.Database.MigrateAsync(ct);

        // Simüle PSP ayrı DbContext ve ayrı migration geçmişi kullanır (ADR 0007).
        var simBuilder = new DbContextOptionsBuilder<PspSimDbContext>();
        PspSimDbContext.Configure(simBuilder, ownerConnectionString);
        await using var sim = new PspSimDbContext(simBuilder.Options);
        sim.Database.SetCommandTimeout(120);
        await sim.Database.MigrateAsync(ct);

        await SyncPermissionsAsync(db, appRole, ct, sim.Model);
    }

    /// <summary>İdempotent: her migrate çağrısında tüm yetkiler yeniden kurulur.</summary>
    public static async Task SyncPermissionsAsync(AppDbContext db, string appRole, CancellationToken ct, params IModel[] additionalModels)
    {
        if (!RoleName().IsMatch(appRole))
            throw new InvalidOperationException("Database:AppUser must be a lowercase PostgreSQL identifier.");

        var roleExists = await db.Database
            .SqlQuery<bool>($"SELECT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = {appRole}) AS \"Value\"")
            .SingleAsync(ct);
        if (!roleExists)
            throw new InvalidOperationException($"Runtime role '{appRole}' does not exist. Create it before running migrations.");

        var tables = additionalModels.Prepend(db.Model).SelectMany(m => m.GetEntityTypes())
            .Select(t => (Schema: t.GetSchema() ?? "public", Table: t.GetTableName()))
            .Where(t => t.Table is not null)
            .Select(t => (t.Schema, Table: t.Table!))
            .Distinct()
            .ToList();
        var schemas = tables.Select(t => t.Schema).Append(AppDbContext.MigrationsHistorySchema).Distinct().Order(StringComparer.Ordinal).ToList();

        // Tanımlayıcılar modelden ve doğrulanmış rol adından gelir; kullanıcı girdisi yoktur.
        var sql = new List<string>();
        foreach (var schema in schemas)
        {
            sql.Add($"REVOKE ALL ON SCHEMA \"{schema}\" FROM \"{appRole}\"");
            sql.Add($"GRANT USAGE ON SCHEMA \"{schema}\" TO \"{appRole}\"");
            sql.Add($"REVOKE ALL ON ALL TABLES IN SCHEMA \"{schema}\" FROM \"{appRole}\"");
            sql.Add($"GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA \"{schema}\" TO \"{appRole}\"");
            sql.Add($"GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA \"{schema}\" TO \"{appRole}\"");
        }

        var known = tables.ToHashSet();
        foreach (var (schema, table) in AppendOnlyTables.Where(known.Contains))
            sql.Add($"REVOKE UPDATE, DELETE, TRUNCATE ON \"{schema}\".\"{table}\" FROM \"{appRole}\"");

        (string Schema, string Table)[] histories = additionalModels.Length == 0
            ? [(AppDbContext.MigrationsHistorySchema, AppDbContext.MigrationsHistoryTable)]
            : [(AppDbContext.MigrationsHistorySchema, AppDbContext.MigrationsHistoryTable), (PspSimDbContext.Schema, PspSimDbContext.MigrationsHistoryTable)];
        foreach (var (schema, table) in histories)
        {
            sql.Add($"REVOKE ALL ON \"{schema}\".\"{table}\" FROM \"{appRole}\"");
            sql.Add($"GRANT SELECT ON \"{schema}\".\"{table}\" TO \"{appRole}\"");
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        foreach (var statement in sql)
            await db.Database.ExecuteSqlRawAsync(statement, ct);
        await tx.CommitAsync(ct);
    }

    [GeneratedRegex("^[a-z_][a-z0-9_]{0,62}$")]
    private static partial Regex RoleName();
}
