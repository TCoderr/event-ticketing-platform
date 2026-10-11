using System.Collections.Concurrent;
using System.Net;
using Loca.Api.Infrastructure.Idempotency;
using Npgsql;

namespace Loca.IntegrationTests.Infrastructure;

/// <summary>
/// Test host'unda kayıtlı failpoint uygulaması. Üretim derlemesinde yalnız no-op sürüm vardır ve bunu
/// HTTP üzerinden tetiklemenin bir yolu yoktur.
/// </summary>
public sealed class TestFailpoints : IFailpoints
{
    private readonly ConcurrentDictionary<string, Func<CancellationToken, Task>> _handlers = new(StringComparer.Ordinal);

    public ValueTask HitAsync(string name, CancellationToken ct = default) =>
        _handlers.TryGetValue(name, out var handler) ? new ValueTask(handler(ct)) : ValueTask.CompletedTask;

    public void On(string name, Func<CancellationToken, Task> handler) => _handlers[name] = handler;
    public void Clear(string name) => _handlers.TryRemove(name, out _);

    /// <summary>Noktaya ilk gelişte istisna fırlatır (ör. commit sonrası yanıt kaybı).</summary>
    public void FailOnce(string name)
    {
        var fired = 0;
        On(name, _ => Interlocked.Exchange(ref fired, 1) == 0
            ? Task.FromException(new FailpointException(name))
            : Task.CompletedTask);
    }

    /// <summary>Noktaya gelen ilk çağrıyı, test bırakana kadar bekletir.</summary>
    public Gate BlockOnce(string name)
    {
        var gate = new Gate();
        var used = 0;
        On(name, async ct =>
        {
            if (Interlocked.Exchange(ref used, 1) != 0) return;
            gate.MarkEntered();
            await gate.Released.WaitAsync(TimeSpan.FromSeconds(30), ct);
        });
        return gate;
    }

    public sealed class Gate
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Entered => _entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        internal Task Released => _released.Task;
        internal void MarkEntered() => _entered.TrySetResult();
        public void Release() => _released.TrySetResult();
    }
}

public sealed class FailpointException(string name) : Exception($"Failpoint '{name}' fired.");

public static class HoldApi
{
    public static string NewKey() => "test-" + Guid.NewGuid().ToString("N");

    public static Task<ApiResponse> HoldSeatsAsync(this TestClient client, Guid sessionId, IEnumerable<Guid> seatIds, string? key = null) =>
        client.PostAsync($"/api/sessions/{sessionId}/holds", new { seatIds = seatIds.ToArray() }, key ?? NewKey());

    public static Task<ApiResponse> HoldGaAsync(this TestClient client, Guid sessionId, string? key = null, params (Guid CategoryId, int Quantity)[] items) =>
        client.PostAsync($"/api/sessions/{sessionId}/holds",
            new { items = items.Select(i => new { categoryId = i.CategoryId, quantity = i.Quantity }).ToArray() }, key ?? NewKey());

    public static Guid HoldId(this ApiResponse response) => response.ShouldBe(HttpStatusCode.Created).Json["id"]!.GetValue<Guid>();
}

/// <summary>Bir sunucu oturumunun kilit beklemeye başladığını pg_stat_activity üzerinden gözler (uyumadan sıralama kurmak için).</summary>
public static class LockWaits
{
    public static async Task UntilSomeoneWaitsAsync(TestDatabase database, string queryFragment, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(20));
        while (DateTime.UtcNow < deadline)
        {
            var waiting = await database.ScalarAsAdminAsync<long>(
                "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock' AND query ILIKE @q",
                new NpgsqlParameter("q", "%" + queryFragment + "%"));
            if (waiting > 0) return;
            await Task.Delay(20);
        }
        throw new TimeoutException($"No session started waiting on a lock for a query containing '{queryFragment}'.");
    }
}
