using System.Security.Cryptography;
using Loca.Api.Infrastructure.Persistence;
using Loca.IntegrationTests.Infrastructure;
using Npgsql;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(PostgresFixture))]
[assembly: CaptureConsole]

namespace Loca.IntegrationTests.Infrastructure;

/// <summary>Bu test koşusunun sahip olduğu PostgreSQL sunucusunun kimliği.</summary>
public sealed record TestRunIdentity(string RunId, string SystemIdentifier, string Host, int Port);

public sealed class TestTargetRejectedException(string message) : Exception(message);

/// <summary>
/// Koşu başına tek PostgreSQL konteyneri. Her test, migration uygulanmış şablondan kopyalanan kendi
/// veritabanını alır. Başka hiçbir sunucuya (geliştirme stack'i, başka projeler) bağlanılmaz.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    public const string Image = "postgres:18.6";
    public const string OwnerRole = "loca_owner";
    public const string AppRole = "loca_app";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder(Image)
        .WithDatabase("postgres")
        .WithUsername("postgres")
        .WithPassword(RandomSecret())
        // Testler sınıf bazında paralel çalışır ve yarış testleri onlarca eşzamanlı bağlantı açar.
        .WithCommand("-c", "max_connections=400")
        .Build();

    private int _databaseCounter;
    private string _ownerPassword = "";
    private string _appPassword = "";

    public TestRunIdentity Identity { get; private set; } = null!;
    public string TemplateDatabase { get; private set; } = "";

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();

        var runId = "r" + RandomNumberGenerator.GetHexString(10, lowercase: true);
        var admin = new NpgsqlConnectionStringBuilder(_container.GetConnectionString());
        await using (var connection = new NpgsqlConnection(admin.ConnectionString))
        {
            await connection.OpenAsync();
            var systemIdentifier = (await Scalar(connection, "SELECT system_identifier::text FROM pg_control_system()"))!;
            Identity = new TestRunIdentity(runId, systemIdentifier, admin.Host!, admin.Port);

            _ownerPassword = RandomSecret();
            _appPassword = RandomSecret();
            // Parolalar bu süreçte üretilen hex değerlerdir; kullanıcı girdisi yoktur.
            await Execute(connection, $"CREATE ROLE {OwnerRole} LOGIN PASSWORD '{_ownerPassword}'");
            await Execute(connection, $"CREATE ROLE {AppRole} LOGIN PASSWORD '{_appPassword}'");

            TemplateDatabase = $"loca_tpl_{runId}";
            await Execute(connection, $"CREATE DATABASE {TemplateDatabase} OWNER {OwnerRole}");
        }

        // Şablonun ilk yazması işarettir; ondan önce yalnız sunucu kimliği doğrulanabilir.
        await TestDatabaseGuard.EnsureOwnedAsync(AdminConnectionString(TemplateDatabase), Identity, requireMarker: false);
        await using (var connection = new NpgsqlConnection(AdminConnectionString(TemplateDatabase)))
        {
            await connection.OpenAsync();
            await Execute(connection, $"CREATE TABLE public.{TestDatabaseGuard.MarkerTable} (run_id text PRIMARY KEY, created_at timestamptz NOT NULL DEFAULT now())");
            await Execute(connection, $"INSERT INTO public.{TestDatabaseGuard.MarkerTable} (run_id) VALUES ('{runId}')");
            await Execute(connection, $"GRANT SELECT ON public.{TestDatabaseGuard.MarkerTable} TO PUBLIC");
        }

        await TestDatabaseGuard.EnsureOwnedAsync(OwnerConnectionString(TemplateDatabase), Identity, requireMarker: true);
        await Migrator.RunAsync(OwnerConnectionString(TemplateDatabase), AppRole, CancellationToken.None);
        NpgsqlConnection.ClearAllPools(); // Şablon kopyalanırken açık bağlantı olmamalı.
    }

    /// <summary>Migration uygulanmış şablondan yeni, boş bir test veritabanı üretir.</summary>
    public Task<TestDatabase> CreateDatabaseAsync() => CreateDatabaseAsync(TemplateDatabase);

    /// <summary>Hiç migration uygulanmamış (yalnız işaret içeren) veritabanı; migration testleri için.</summary>
    public async Task<TestDatabase> CreateUnmigratedDatabaseAsync()
    {
        var database = await CreateDatabaseAsync("template0", withMarker: true);
        return database;
    }

    private async Task<TestDatabase> CreateDatabaseAsync(string template, bool withMarker = false)
    {
        var name = $"loca_t_{Identity.RunId}_{Interlocked.Increment(ref _databaseCounter):D4}";
        var adminServer = AdminConnectionString("postgres");
        TestDatabaseGuard.EnsureServerEndpoint(adminServer, Identity);
        await using (var connection = new NpgsqlConnection(adminServer))
        {
            await connection.OpenAsync();
            await TestDatabaseGuard.EnsureServerIdentityAsync(connection, Identity);
            await Execute(connection, $"CREATE DATABASE {name} OWNER {OwnerRole} TEMPLATE {template}");
        }

        if (withMarker)
        {
            await using var connection = new NpgsqlConnection(AdminConnectionString(name));
            await connection.OpenAsync();
            await TestDatabaseGuard.EnsureServerIdentityAsync(connection, Identity);
            await Execute(connection, $"CREATE TABLE public.{TestDatabaseGuard.MarkerTable} (run_id text PRIMARY KEY, created_at timestamptz NOT NULL DEFAULT now())");
            await Execute(connection, $"INSERT INTO public.{TestDatabaseGuard.MarkerTable} (run_id) VALUES ('{Identity.RunId}')");
            await Execute(connection, $"GRANT SELECT ON public.{TestDatabaseGuard.MarkerTable} TO PUBLIC");
        }

        var database = new TestDatabase(name, Identity, AppConnectionString(name), OwnerConnectionString(name), AdminConnectionString(name));
        await TestDatabaseGuard.EnsureOwnedAsync(database.OwnerConnectionString, Identity, requireMarker: true);
        return database;
    }

    public string AdminConnectionString(string database) =>
        new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = database, Pooling = false }.ConnectionString;

    private string OwnerConnectionString(string database) => Build(database, OwnerRole, _ownerPassword, pooling: false);

    private string AppConnectionString(string database) => Build(database, AppRole, _appPassword, pooling: true);

    private string Build(string database, string user, string password, bool pooling) =>
        new NpgsqlConnectionStringBuilder
        {
            Host = Identity.Host, Port = Identity.Port, Database = database, Username = user, Password = password,
            Pooling = pooling, MaxPoolSize = 60, Timeout = 10, IncludeErrorDetail = true,
        }.ConnectionString;

    public async ValueTask DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await _container.DisposeAsync();
    }

    private static string RandomSecret() => RandomNumberGenerator.GetHexString(32, lowercase: true);

    private static async Task Execute(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string?> Scalar(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return (await command.ExecuteScalarAsync())?.ToString();
    }
}

/// <summary>
/// Test hedefi koruması. Ad öneki tek başına kanıt sayılmaz: uç nokta, sunucu kimliği ve koşuya ait
/// işaret birlikte doğrulanır. Doğrulama yalnız okuma yapar.
/// </summary>
public static class TestDatabaseGuard
{
    public const string MarkerTable = "loca_test_marker";

    public static void EnsureServerEndpoint(string connectionString, TestRunIdentity identity)
    {
        var target = new NpgsqlConnectionStringBuilder(connectionString);
        if (!string.Equals(target.Host, identity.Host, StringComparison.OrdinalIgnoreCase) || target.Port != identity.Port)
            throw new TestTargetRejectedException("Refusing to use a database server that was not started by this test run (endpoint mismatch).");
    }

    public static async Task EnsureServerIdentityAsync(NpgsqlConnection openConnection, TestRunIdentity identity)
    {
        await using var command = new NpgsqlCommand("SELECT system_identifier::text FROM pg_control_system()", openConnection);
        var actual = (await command.ExecuteScalarAsync())?.ToString();
        if (!string.Equals(actual, identity.SystemIdentifier, StringComparison.Ordinal))
            throw new TestTargetRejectedException("Refusing to use a database server that was not started by this test run (system identifier mismatch).");
    }

    public static async Task EnsureOwnedAsync(string connectionString, TestRunIdentity identity, bool requireMarker)
    {
        EnsureServerEndpoint(connectionString, identity); // Bağlanmadan önce.

        var builder = new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false };
        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        await using (var readOnly = new NpgsqlCommand("SET default_transaction_read_only = on", connection))
            await readOnly.ExecuteNonQueryAsync();

        await EnsureServerIdentityAsync(connection, identity);
        if (!requireMarker) return;

        await using var exists = new NpgsqlCommand($"SELECT to_regclass('public.{MarkerTable}') IS NOT NULL", connection);
        if (await exists.ExecuteScalarAsync() is not true)
            throw new TestTargetRejectedException("Refusing to use a database without this run's ownership marker.");

        await using var marker = new NpgsqlCommand($"SELECT count(*) FROM public.{MarkerTable} WHERE run_id = @run", connection);
        marker.Parameters.AddWithValue("run", identity.RunId);
        if (Convert.ToInt64(await marker.ExecuteScalarAsync()) != 1)
            throw new TestTargetRejectedException("Refusing to use a database whose ownership marker belongs to a different run.");
    }
}

public sealed class TestDatabase(string name, TestRunIdentity identity, string appConnectionString, string ownerConnectionString, string adminConnectionString)
{
    public string Name { get; } = name;
    public TestRunIdentity Identity { get; } = identity;
    public string AppConnectionString { get; } = appConnectionString;
    public string OwnerConnectionString { get; } = ownerConnectionString;

    /// <summary>Süper kullanıcı bağlantısı: yalnız test düzeneği kurmak (ör. kullanıcıyı yönetici yapmak) için.</summary>
    public async Task<int> ExecuteAsAdminAsync(string sql, params NpgsqlParameter[] parameters)
    {
        await TestDatabaseGuard.EnsureOwnedAsync(adminConnectionString, Identity, requireMarker: true);
        await using var connection = new NpgsqlConnection(adminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddRange(parameters);
        return await command.ExecuteNonQueryAsync();
    }

    public async Task<T?> ScalarAsAdminAsync<T>(string sql, params NpgsqlParameter[] parameters)
    {
        await using var connection = new NpgsqlConnection(adminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddRange(parameters);
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? default : (T)Convert.ChangeType(value, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T));
    }

    /// <summary>Çalışma zamanı rolüyle (loca_app) ham SQL; yetki sınırı testleri için.</summary>
    public async Task<int> ExecuteAsAppAsync(string sql)
    {
        await TestDatabaseGuard.EnsureOwnedAsync(AppConnectionString, Identity, requireMarker: true);
        await using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(AppConnectionString) { Pooling = false }.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteNonQueryAsync();
    }
}
