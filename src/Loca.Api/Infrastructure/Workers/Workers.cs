namespace Loca.Api.Infrastructure.Workers;

/// <summary>Bir tur iş yapıp işlediği öğe sayısını döndüren, tekrar çalıştırılması güvenli işlemci.</summary>
public interface IBatchProcessor
{
    string Name { get; }
    Task<int> RunOnceAsync(CancellationToken ct);
}

public sealed class WorkerOptions
{
    public const string Section = "Workers";
    public bool Enabled { get; set; } = true;
    public int IdleDelayMs { get; set; } = 1000;
    public int ErrorDelayMs { get; set; } = 5000;
    public int ShutdownGraceSeconds { get; set; } = 15;
}

/// <summary>Süreç içi uyandırma: commit sonrası ilgili worker'ı yoklama aralığını beklemeden tetikler.</summary>
public sealed class WorkerSignal
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    public void Notify()
    {
        if (_signal.CurrentCount == 0)
        {
            try { _signal.Release(); }
            catch (SemaphoreFullException) { /* Zaten uyandırılmış. */ }
        }
    }

    public async Task WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        try { await _signal.WaitAsync(timeout, ct); }
        catch (OperationCanceledException) { /* Kapanış. */ }
    }
}

/// <summary>
/// Bir <see cref="IBatchProcessor"/>'ı döngüde çalıştırır. Her tur kendi DI kapsamındadır. Kapanışta
/// eldeki tur iptal belirteciyle sonlandırılır; yarım kalan iş ya transaction rollback'i ya da lease
/// süresinin dolması ile başka bir tur tarafından devralınır. Fire-and-forget yoktur.
/// </summary>
public sealed class ProcessorWorker<TProcessor>(
    IServiceScopeFactory scopes, Microsoft.Extensions.Options.IOptions<WorkerOptions> options, WorkerSignal<TProcessor> signal,
    ILogger<ProcessorWorker<TProcessor>> logger) : BackgroundService where TProcessor : class, IBatchProcessor
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = options.Value;
        if (!opts.Enabled) return;

        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = TimeSpan.FromMilliseconds(opts.IdleDelayMs);
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var processor = scope.ServiceProvider.GetRequiredService<TProcessor>();
                var processed = await processor.RunOnceAsync(stoppingToken);
                if (processed > 0) delay = TimeSpan.Zero; // İş varken ara vermeden devam et.
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                // İstisna mesajı veri içerebileceği için yalnız türü loglanır.
                logger.LogError("Worker {Worker} iteration failed with {ExceptionType}; retrying after a delay.", typeof(TProcessor).Name, e.GetType().Name);
                delay = TimeSpan.FromMilliseconds(opts.ErrorDelayMs);
            }

            if (delay > TimeSpan.Zero) await signal.Inner.WaitAsync(delay, stoppingToken);
        }
    }
}

/// <summary>İşlemci türüne özel uyandırma sinyali.</summary>
public sealed class WorkerSignal<TProcessor>
{
    public WorkerSignal Inner { get; } = new();
    public void Notify() => Inner.Notify();
}

public static class WorkerRegistration
{
    public static IServiceCollection AddProcessorWorker<TProcessor>(this IServiceCollection services) where TProcessor : class, IBatchProcessor
    {
        services.AddScoped<TProcessor>();
        services.AddSingleton<WorkerSignal<TProcessor>>();
        services.AddHostedService<ProcessorWorker<TProcessor>>();
        return services;
    }
}
