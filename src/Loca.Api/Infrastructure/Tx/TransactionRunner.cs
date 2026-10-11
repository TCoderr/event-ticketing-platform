using System.Data;
using Loca.Api.Infrastructure.Http;
using Loca.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Loca.Api.Infrastructure.Tx;

/// <summary>
/// READ COMMITTED transaction çalıştırır. Deadlock (40P01) ve serialization (40001) hatalarında
/// transaction'ı baştan, jitter'lı bekleme ile sınırlı sayıda yeniden dener (ADR 0002).
/// </summary>
public interface ITransactionRunner
{
    Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct);
}

public sealed class TransactionOptions
{
    public const string Section = "Transactions";
    public int MaxAttempts { get; set; } = 3;
    public int LockTimeoutMs { get; set; } = 5000;
}

/// <summary>Süreç ömrü boyunca sayaçlar; metrik ve testler için.</summary>
public sealed class TransactionStats
{
    private long _retries;
    public long Retries => Interlocked.Read(ref _retries);
    internal void RecordRetry() => Interlocked.Increment(ref _retries);
}

internal sealed class TransactionRunner(
    AppDbContext db, Microsoft.Extensions.Options.IOptions<TransactionOptions> options, TransactionStats stats,
    ILogger<TransactionRunner> logger) : ITransactionRunner
{
    public async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is not null)
            throw new InvalidOperationException("Nested transactions are not supported; a transaction is already active.");

        var opts = options.Value;
        for (var attempt = 1; ; attempt++)
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            try
            {
                // set_config(..., is_local: true) yalnız bu transaction için geçerlidir (SET LOCAL eşdeğeri, parametreli).
                var lockTimeout = opts.LockTimeoutMs.ToString(System.Globalization.CultureInfo.InvariantCulture);
                await db.Database.ExecuteSqlAsync($"SELECT set_config('lock_timeout', {lockTimeout}, true)", ct);
                var result = await work(ct);
                await tx.CommitAsync(ct);
                return result;
            }
            catch (Exception e) when (IsRetryable(e) && attempt < opts.MaxAttempts)
            {
                await SafeRollbackAsync(tx);
                stats.RecordRetry();
                logger.LogWarning("Transaction attempt {Attempt} failed with a retryable database error; retrying.", attempt);
                await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(5, 40) * attempt), ct);
            }
            catch (Exception e) when (IsRetryable(e))
            {
                await SafeRollbackAsync(tx);
                throw ApiErrors.Unavailable("The operation could not be completed due to contention. Retry the request.");
            }
            catch (Exception e) when (IsLockTimeout(e))
            {
                await SafeRollbackAsync(tx);
                throw new ApiException(StatusCodes.Status409Conflict, Loca.Domain.ErrorCodes.RequestInProgress,
                    "A conflicting operation is still in progress. Retry shortly.",
                    new Dictionary<string, object?> { ["retryAfterSeconds"] = 1 });
            }
            catch
            {
                await SafeRollbackAsync(tx);
                throw;
            }
        }
    }

    private static async Task SafeRollbackAsync(Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx)
    {
        try { await tx.RollbackAsync(CancellationToken.None); }
        catch { /* Bağlantı koptuysa rollback sunucuda zaten gerçekleşir. */ }
    }

    public static bool IsRetryable(Exception exception) =>
        FindPostgres(exception) is { SqlState: PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure };

    public static bool IsLockTimeout(Exception exception) =>
        FindPostgres(exception) is { SqlState: PostgresErrorCodes.LockNotAvailable };

    public static PostgresException? FindPostgres(Exception? exception)
    {
        for (var e = exception; e is not null; e = e.InnerException)
            if (e is PostgresException pg) return pg;
        return null;
    }

    public static bool IsUniqueViolation(Exception exception, string? constraintName = null) =>
        FindPostgres(exception) is { SqlState: PostgresErrorCodes.UniqueViolation } pg &&
        (constraintName is null || string.Equals(pg.ConstraintName, constraintName, StringComparison.Ordinal));
}
