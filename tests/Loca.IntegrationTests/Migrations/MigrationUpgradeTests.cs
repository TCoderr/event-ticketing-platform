using Loca.Api.Infrastructure.Persistence;
using Loca.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Loca.IntegrationTests.Migrations;

/// <summary>
/// Migration'lar yalnız betik üretilerek değil, gerçekten uygulanarak doğrulanır:
/// boş kurulum ve önceki aşamanın sentetik verili durumundan yükseltme.
/// </summary>
public sealed class MigrationUpgradeTests(PostgresFixture postgres)
{
    private static AppDbContext OwnerContext(TestDatabase database)
    {
        var builder = new DbContextOptionsBuilder<AppDbContext>();
        AppDbContext.Configure(builder, database.OwnerConnectionString);
        return new AppDbContext(builder.Options);
    }

    private static async Task MigrateToAsync(TestDatabase database, string migration)
    {
        await TestDatabaseGuard.EnsureOwnedAsync(database.OwnerConnectionString, database.Identity, requireMarker: true);
        await using var db = OwnerContext(database);
        await db.GetService<IMigrator>().MigrateAsync(migration);
    }

    [Fact]
    public async Task Empty_database_installs_every_migration_and_grants_runtime_permissions()
    {
        var database = await postgres.CreateUnmigratedDatabaseAsync();
        await Migrator.RunAsync(database.OwnerConnectionString, PostgresFixture.AppRole, CancellationToken.None);

        await using var db = OwnerContext(database);
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.Equal(db.Database.GetMigrations().Count(), (await db.Database.GetAppliedMigrationsAsync()).Count());

        // Çalışma zamanı rolü yeni kurulumda da yalnız DML yapabilir.
        Assert.Equal(1, await database.ExecuteAsAppAsync(
            "INSERT INTO ops.audit_log (occurred_at, actor_kind, action, entity_type, entity_id) VALUES (now(), 'System', 'install.check', 'test', '1')"));
        var denied = await Assert.ThrowsAsync<PostgresException>(() => database.ExecuteAsAppAsync("CREATE TABLE catalog.sneaky (id int)"));
        Assert.Equal("42501", denied.SqlState);
    }

    [Fact]
    public async Task Running_the_migrator_twice_is_a_no_op()
    {
        var database = await postgres.CreateUnmigratedDatabaseAsync();
        await Migrator.RunAsync(database.OwnerConnectionString, PostgresFixture.AppRole, CancellationToken.None);
        var before = await database.ScalarAsAdminAsync<long>("SELECT count(*) FROM ops.__ef_migrations_history");
        await Migrator.RunAsync(database.OwnerConnectionString, PostgresFixture.AppRole, CancellationToken.None);
        Assert.Equal(before, await database.ScalarAsAdminAsync<long>("SELECT count(*) FROM ops.__ef_migrations_history"));
    }

    [Fact]
    public async Task Upgrade_from_the_identity_stage_preserves_existing_rows()
    {
        var database = await postgres.CreateUnmigratedDatabaseAsync();
        await MigrateToAsync(database, "InitialIdentity");
        Assert.False(await TableExistsAsync(database, "catalog", "events"));

        var userId = Guid.CreateVersion7();
        var organizationId = Guid.CreateVersion7();
        await database.ExecuteAsAdminAsync(
            """
            INSERT INTO identity.users (id, user_name, normalized_user_name, email, normalized_email, email_confirmed, phone_number_confirmed,
                two_factor_enabled, lockout_enabled, access_failed_count, display_name, is_platform_admin, is_demo, created_at, security_stamp)
            VALUES (@u, 'eski@loca.test', 'ESKI@LOCA.TEST', 'eski@loca.test', 'ESKI@LOCA.TEST', true, false, false, true, 0, 'Eski Kullanıcı', false, false, now(), 'stamp');
            INSERT INTO identity.organizations (id, name, slug, status, is_demo, created_at) VALUES (@o, 'Eski Organizasyon', 'eski-organizasyon', 'Active', false, now());
            INSERT INTO identity.organization_members (organization_id, user_id, role, created_at) VALUES (@o, @u, 'OrganizerAdmin', now());
            INSERT INTO ops.audit_log (occurred_at, actor_kind, action, entity_type, entity_id) VALUES (now(), 'System', 'legacy.row', 'organization', @o::text);
            """,
            new NpgsqlParameter("u", userId), new NpgsqlParameter("o", organizationId));

        await Migrator.RunAsync(database.OwnerConnectionString, PostgresFixture.AppRole, CancellationToken.None);

        Assert.True(await TableExistsAsync(database, "catalog", "events"));
        Assert.Equal("Eski Kullanıcı", await database.ScalarAsAdminAsync<string>("SELECT display_name FROM identity.users WHERE id = @u", new NpgsqlParameter("u", userId)));
        Assert.Equal("OrganizerAdmin", await database.ScalarAsAdminAsync<string>("SELECT role FROM identity.organization_members WHERE organization_id = @o", new NpgsqlParameter("o", organizationId)));
        Assert.Equal(1, await database.ScalarAsAdminAsync<long>("SELECT count(*) FROM ops.audit_log WHERE action = 'legacy.row'"));

        // Yükseltilen veritabanı yeni şemayla kullanılabilir: eski organizasyona mekan eklenebilir.
        Assert.Equal(1, await database.ExecuteAsAppAsync(
            $"INSERT INTO catalog.venues (id, organization_id, name, city, address, timezone, created_at) VALUES (gen_random_uuid(), '{organizationId}', 'Salon', 'İstanbul', 'Adres', 'Europe/Istanbul', now())"));
        await using var db = OwnerContext(database);
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }

    [Fact]
    public async Task Upgrade_from_the_catalog_stage_keeps_existing_sessions_sellable_end_to_end()
    {
        // Katalog aşamasının şeması + sentetik veri: organizasyon, mekan, plan, yayınlanmış etkinlik ve koltuklu seans.
        var database = await postgres.CreateUnmigratedDatabaseAsync();
        await MigrateToAsync(database, "Catalog");
        Assert.False(await TableExistsAsync(database, "inventory", "reservations"));
        Assert.False(await TableExistsAsync(database, "orders", "orders"));

        var clock = new ManualClock();
        Guid organizationId = Guid.CreateVersion7(), venueId = Guid.CreateVersion7(), layoutId = Guid.CreateVersion7(),
            eventId = Guid.CreateVersion7(), sessionId = Guid.CreateVersion7(), seatId = Guid.CreateVersion7();
        await database.ExecuteAsAdminAsync(
            """
            INSERT INTO identity.organizations (id, name, slug, status, is_demo, created_at) VALUES (@org, 'Eski Organizasyon', 'eski-org', 'Active', false, @now);
            INSERT INTO catalog.venues (id, organization_id, name, city, address, timezone, created_at) VALUES (@venue, @org, 'Eski Salon', 'İstanbul', 'Adres', 'Europe/Istanbul', @now);
            INSERT INTO catalog.seat_layouts (id, venue_id, version, name, seat_count, created_at) VALUES (@layout, @venue, 1, 'Plan', 1, @now);
            INSERT INTO catalog.layout_seats (id, layout_id, section, row_label, seat_number, x, y, tier_code) VALUES (gen_random_uuid(), @layout, 'Salon', 'A', 1, 0, 0, 'A');
            INSERT INTO catalog.events (id, organization_id, title, slug, description, category, status, is_demo, created_at, updated_at)
                VALUES (@event, @org, 'Eski Konser', 'eski-konser', 'Yükseltme öncesi oluşturulmuş etkinlik.', 'Concert', 'Published', false, @now, @now);
            INSERT INTO catalog.sessions (id, event_id, organization_id, venue_id, city, timezone, starts_at, sales_start_at, sales_end_at, sales_mode, status,
                    hold_ttl_seconds, layout_id, demo_simulator_enabled, created_at)
                VALUES (@session, @event, @org, @venue, 'İstanbul', 'Europe/Istanbul', @starts, @now - interval '1 hour', @starts, 'ReservedSeating', 'Published', 600, @layout, false, @now);
            INSERT INTO inventory.session_seats (id, session_id, section, row_label, seat_number, x, y, tier_code, price_minor, status)
                VALUES (@seat, @session, 'Salon', 'A', 1, 0, 0, 'A', 45000, 'Available');
            """,
            new NpgsqlParameter("org", organizationId), new NpgsqlParameter("venue", venueId), new NpgsqlParameter("layout", layoutId),
            new NpgsqlParameter("event", eventId), new NpgsqlParameter("session", sessionId), new NpgsqlParameter("seat", seatId),
            new NpgsqlParameter("now", clock.Now), new NpgsqlParameter("starts", clock.Now.AddDays(10)));

        await Migrator.RunAsync(database.OwnerConnectionString, PostgresFixture.AppRole, CancellationToken.None);

        // Yükseltilmiş veritabanında uygulama çalışır: eski seans keşifte görünür, tutulur, ödenir ve bileti üretilir.
        await using var factory = new LocaFactory(database, clock);
        var customer = factory.NewClient();
        await customer.RegisterAsync("yukseltme@loca.test");
        (await customer.LoginAsync("yukseltme@loca.test")).ShouldBe(System.Net.HttpStatusCode.OK);

        var page = (await customer.GetAsync("/api/catalog/sessions")).ShouldBe(System.Net.HttpStatusCode.OK);
        Assert.Equal(sessionId, Assert.Single(page.Json["items"]!.AsArray())!["sessionId"]!.GetValue<Guid>());

        var holdId = (await customer.HoldSeatsAsync(sessionId, [seatId])).HoldId();
        var orderId = (await customer.CheckoutAsync(holdId)).OrderId();
        for (var i = 0; i < 10; i++)
        {
            var worked = await factory.WithScopeAsync(sp => Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
                .GetRequiredService<Loca.Api.Infrastructure.Outbox.OutboxProcessor>(sp).RunOnceAsync(CancellationToken.None));
            if (worked == 0) break;
        }

        Assert.Equal("Paid", (await customer.OrderAsync(orderId)).OrderStatus());
        Assert.Single((await customer.GetAsync("/api/tickets")).Json.AsArray());
        Assert.Equal("Sold", await database.ScalarAsAdminAsync<string>("SELECT status FROM inventory.session_seats WHERE id = @id", new NpgsqlParameter("id", seatId)));
        using (var pooled = new NpgsqlConnection(database.AppConnectionString)) NpgsqlConnection.ClearPool(pooled);
    }

    private static async Task<bool> TableExistsAsync(TestDatabase database, string schema, string table) =>
        await database.ScalarAsAdminAsync<bool>("SELECT to_regclass(@name) IS NOT NULL", new NpgsqlParameter("name", $"{schema}.{table}"));
}
