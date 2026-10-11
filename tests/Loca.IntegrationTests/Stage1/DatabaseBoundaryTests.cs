using System.Net;
using Loca.Api.Infrastructure.Persistence;
using Loca.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Loca.IntegrationTests.Stage1;

/// <summary>Çalışma zamanı rolünün (loca_app) gerçek veritabanındaki yetki sınırları.</summary>
public sealed class DatabaseRoleTests(PostgresFixture postgres) : IntegrationTest(postgres)
{
    private const string InsufficientPrivilege = "42501";

    [Theory]
    [InlineData("CREATE TABLE ops.sneaky (id int)")]
    [InlineData("CREATE SCHEMA sneaky")]
    [InlineData("ALTER TABLE identity.users ADD COLUMN sneaky int")]
    [InlineData("DROP TABLE ops.audit_log")]
    [InlineData("UPDATE ops.audit_log SET action = 'tampered'")]
    [InlineData("DELETE FROM ops.audit_log")]
    [InlineData("TRUNCATE ops.audit_log")]
    [InlineData("TRUNCATE identity.users CASCADE")]
    [InlineData("INSERT INTO ops.__ef_migrations_history (migration_id, product_version) VALUES ('x', 'y')")]
    [InlineData("DELETE FROM ops.__ef_migrations_history")]
    public async Task Runtime_role_is_denied(string sql)
    {
        var error = await Assert.ThrowsAsync<PostgresException>(() => Database.ExecuteAsAppAsync(sql));
        Assert.Equal(InsufficientPrivilege, error.SqlState);
    }

    [Fact]
    public async Task Runtime_role_can_append_to_audit_log_and_is_not_privileged()
    {
        var inserted = await Database.ExecuteAsAppAsync(
            "INSERT INTO ops.audit_log (occurred_at, actor_kind, action, entity_type, entity_id) VALUES (now(), 'System', 'test.append', 'test', '1')");
        Assert.Equal(1, inserted);

        var flags = await Database.ScalarAsAdminAsync<string>(
            "SELECT rolsuper::text || ',' || rolcreatedb::text || ',' || rolcreaterole::text FROM pg_roles WHERE rolname = 'loca_app'");
        Assert.Equal("false,false,false", flags);
    }

    [Fact]
    public async Task Model_and_migrations_are_consistent_and_fully_applied()
    {
        await Factory.WithDbAsync(async db =>
        {
            Assert.False(db.Database.HasPendingModelChanges(), "The EF model has changes that are not captured in a migration.");
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            return 0;
        });
    }

    [Fact]
    public async Task Readiness_reports_ready_with_a_migrated_database()
    {
        var client = Factory.NewClient();
        (await client.GetAsync("/health/live")).ShouldBe(HttpStatusCode.OK);
        (await client.GetAsync("/health/ready")).ShouldBe(HttpStatusCode.OK);
    }
}

public sealed class DatabaseOutageTests(PostgresFixture postgres) : IAsyncLifetime
{
    private TestDatabase _database = null!;
    private LocaFactory _factory = null!;

    public async ValueTask InitializeAsync()
    {
        _database = await postgres.CreateDatabaseAsync();
        // Kimsenin dinlemediği bir uç: bağlantı reddedilir; hiçbir veritabanına dokunulmaz.
        var unreachable = new NpgsqlConnectionStringBuilder(_database.AppConnectionString) { Host = "127.0.0.1", Port = 1, Timeout = 2, Pooling = false };
        _factory = new LocaFactory(_database, new ManualClock(), new Dictionary<string, string?>
        {
            ["Database:AppConnectionString"] = unreachable.ConnectionString,
        });
    }

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task When_the_database_is_unavailable_readiness_is_503_liveness_stays_200_and_no_request_reports_success()
    {
        var client = _factory.NewClient();

        (await client.GetAsync("/health/live")).ShouldBe(HttpStatusCode.OK);
        var ready = (await client.GetAsync("/health/ready")).ShouldBe(HttpStatusCode.ServiceUnavailable);
        Assert.Equal("database_unavailable", ready.Json["reason"]!.GetValue<string>());

        await client.FetchCsrfAsync();
        (await client.PostAsync("/api/auth/login", new { email = "kimse@loca.test", password = "Deneme-Parola-2026" }))
            .ShouldFail(HttpStatusCode.ServiceUnavailable, "service_unavailable");
    }
}

public sealed class TestDatabaseGuardTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Guard_rejects_a_database_without_this_runs_marker_and_writes_nothing()
    {
        var maintenance = postgres.AdminConnectionString("postgres");
        var before = await CountRelations(maintenance);

        await Assert.ThrowsAsync<TestTargetRejectedException>(() =>
            TestDatabaseGuard.EnsureOwnedAsync(maintenance, postgres.Identity, requireMarker: true));

        Assert.Equal(before, await CountRelations(maintenance));
    }

    [Fact]
    public async Task Guard_rejects_a_marker_from_a_different_run()
    {
        var database = await postgres.CreateDatabaseAsync();
        var foreignRun = postgres.Identity with { RunId = "r0000000000" };

        await Assert.ThrowsAsync<TestTargetRejectedException>(() =>
            TestDatabaseGuard.EnsureOwnedAsync(database.OwnerConnectionString, foreignRun, requireMarker: true));
    }

    [Fact]
    public async Task Guard_rejects_a_different_server_identity()
    {
        var database = await postgres.CreateDatabaseAsync();
        var foreignServer = postgres.Identity with { SystemIdentifier = "1" };

        await Assert.ThrowsAsync<TestTargetRejectedException>(() =>
            TestDatabaseGuard.EnsureOwnedAsync(database.OwnerConnectionString, foreignServer, requireMarker: true));
    }

    [Fact]
    public async Task Guard_rejects_a_foreign_endpoint_before_opening_a_connection()
    {
        // 127.0.0.1:1'de dinleyen yok; koruma bağlanmayı deneseydi bağlantı hatası alırdı, reddetme istisnası değil.
        var foreign = new NpgsqlConnectionStringBuilder(postgres.AdminConnectionString("postgres")) { Host = "127.0.0.1", Port = 1 };

        await Assert.ThrowsAsync<TestTargetRejectedException>(() =>
            TestDatabaseGuard.EnsureOwnedAsync(foreign.ConnectionString, postgres.Identity, requireMarker: true));
    }

    private static async Task<long> CountRelations(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT count(*) FROM pg_class WHERE relnamespace NOT IN (SELECT oid FROM pg_namespace WHERE nspname LIKE 'pg_%' OR nspname = 'information_schema')", connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
}
