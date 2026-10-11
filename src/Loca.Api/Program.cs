using Loca.Api;

// Kipler: (varsayılan) serve | migrate | seed-demo
if (args.Length > 0 && Cli.IsCommand(args[0]))
    return await Cli.RunAsync(args);

var builder = WebApplication.CreateBuilder(args);
LocaApp.ConfigureServices(builder);
var app = builder.Build();
LocaApp.ConfigurePipeline(app);
await app.RunAsync();
return 0;

public partial class Program;
