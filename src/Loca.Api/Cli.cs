using Loca.Api.Infrastructure.Config;
using Loca.Api.Infrastructure.Persistence;
using Loca.Api.Modules.Operations;

namespace Loca.Api;

/// <summary>Sunucu dışı kipler. Aynı imaj <c>migrate</c>, <c>seed-demo</c> ve <c>healthcheck</c> için de kullanılır.</summary>
public static class Cli
{
    private static readonly string[] Commands = ["migrate", "seed-demo", "healthcheck"];

    public static bool IsCommand(string arg) => Commands.Contains(arg, StringComparer.Ordinal);

    public static async Task<int> RunAsync(string[] args)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        try
        {
            return args[0] switch
            {
                "migrate" => await MigrateAsync(cts.Token),
                "seed-demo" => await SeedDemoAsync(cts.Token),
                "healthcheck" => await HealthCheckAsync(cts.Token),
                _ => 2,
            };
        }
        catch (Exception e)
        {
            // Bağlantı dizesi veya secret içerebilecek ayrıntı yazılmaz.
            Console.Error.WriteLine($"{args[0]}: failed ({e.GetType().Name}): {Sanitize(e.Message)}");
            return 1;
        }
    }

    private static IConfigurationRoot LoadConfiguration() => new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: true)
        .AddEnvironmentVariables()
        .Build();

    private static async Task<int> MigrateAsync(CancellationToken ct)
    {
        var database = LoadConfiguration().GetSection(DatabaseOptions.Section).Get<DatabaseOptions>() ?? new DatabaseOptions();
        await Migrator.RunAsync(database.BuildOwnerConnectionString(), database.AppUser, ct);
        Console.WriteLine("migrate: completed");
        return 0;
    }

    private static async Task<int> SeedDemoAsync(CancellationToken ct)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration["Workers:Enabled"] = "false";
        LocaApp.ConfigureServices(builder);
        await using var app = builder.Build();
        await using var scope = app.Services.CreateAsyncScope();
        var report = await scope.ServiceProvider.GetRequiredService<DemoSeeder>().RunAsync(ct);
        Console.WriteLine($"seed-demo: completed (created={report.Created}, existing={report.Existing})");
        return 0;
    }

    /// <summary>Konteyner healthcheck'i: imajda curl/wget bulunmadığı için süreç kendi hazır olma ucunu yoklar.</summary>
    private static async Task<int> HealthCheckAsync(CancellationToken ct)
    {
        var hosting = LoadConfiguration().GetSection(HostingOptions.Section).Get<HostingOptions>() ?? new HostingOptions();
        if (hosting.InternalHttpPort <= 0) return 1;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        using var response = await http.GetAsync($"http://127.0.0.1:{hosting.InternalHttpPort}/health/ready", ct);
        return response.IsSuccessStatusCode ? 0 : 1;
    }

    private static string Sanitize(string message) =>
        message.Contains("Password", StringComparison.OrdinalIgnoreCase) ? "details withheld" : message;
}
