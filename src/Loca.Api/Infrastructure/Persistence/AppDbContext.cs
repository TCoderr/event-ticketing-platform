using Loca.Api.Infrastructure.Idempotency;
using Loca.Api.Infrastructure.Outbox;
using Loca.Api.Modules.Catalog;
using Loca.Api.Modules.Identity;
using Loca.Api.Modules.Inventory;
using Loca.Api.Modules.Operations;
using Loca.Api.Modules.Orders;
using Loca.Api.Modules.Tickets;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Loca.Api.Infrastructure.Persistence;

/// <summary>
/// Tek DbContext; her modül kendi şemasını ve eşlemesini tanımlar (ADR 0001).
/// Bir modülün tablolarına yalnız o modülün servisleri yazar.
/// </summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : IdentityUserContext<AppUser, Guid>(options)
{
    public const string MigrationsHistoryTable = "__ef_migrations_history";
    public const string MigrationsHistorySchema = "ops";

    // identity
    public DbSet<UserSession> UserSessions => Set<UserSession>();
    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<OrganizationMember> OrganizationMembers => Set<OrganizationMember>();

    // catalog
    public DbSet<Venue> Venues => Set<Venue>();
    public DbSet<SeatLayout> SeatLayouts => Set<SeatLayout>();
    public DbSet<LayoutSeat> LayoutSeats => Set<LayoutSeat>();
    public DbSet<Event> Events => Set<Event>();
    public DbSet<Session> Sessions => Set<Session>();

    // inventory
    public DbSet<SessionSeat> SessionSeats => Set<SessionSeat>();
    public DbSet<GaCategory> GaCategories => Set<GaCategory>();
    public DbSet<Reservation> Reservations => Set<Reservation>();
    public DbSet<ReservationLine> ReservationLines => Set<ReservationLine>();

    // orders
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<Refund> Refunds => Set<Refund>();
    public DbSet<ReconciliationRun> ReconciliationRuns => Set<ReconciliationRun>();
    public DbSet<SessionCancellationJob> SessionCancellationJobs => Set<SessionCancellationJob>();

    // tickets
    public DbSet<Ticket> Tickets => Set<Ticket>();
    public DbSet<GateAssignment> GateAssignments => Set<GateAssignment>();
    public DbSet<ScanLogEntry> ScanLog => Set<ScanLogEntry>();

    // ops
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();
    public DbSet<AuditEntry> AuditLog => Set<AuditEntry>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        IdentityModel.Configure(builder);
        CatalogModel.Configure(builder);
        InventoryModel.ConfigureStock(builder);
        ReservationModel.Configure(builder);
        IdempotencyModel.Configure(builder);
        OrdersModel.Configure(builder);
        TicketsModel.Configure(builder);
        CancellationModel.Configure(builder);
        OutboxModel.Configure(builder);
        OperationsModel.ConfigureAudit(builder);
    }

    public static void Configure(DbContextOptionsBuilder builder, string connectionString)
    {
        builder
            .UseNpgsql(connectionString, npgsql => npgsql
                .MigrationsHistoryTable(MigrationsHistoryTable, MigrationsHistorySchema)
                .CommandTimeout(15))
            .UseSnakeCaseNamingConvention();
    }
}

/// <summary><c>dotnet ef</c> için. Model üretimi veritabanına bağlanmaz; bağlantı dizesi yer tutucudur.</summary>
internal sealed class DesignTimeAppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("LOCA_DESIGN_CONNECTION")
            ?? "Host=127.0.0.1;Port=1;Database=design_time_only;Username=design_time_only";
        var builder = new DbContextOptionsBuilder<AppDbContext>();
        AppDbContext.Configure(builder, connectionString);
        return new AppDbContext(builder.Options);
    }
}
