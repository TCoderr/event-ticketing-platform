using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Loca.IntegrationTests.Infrastructure;

/// <summary>Uygulamanın yazdığı tüm log satırlarını (biçimlenmiş mesaj + istisna metni + kapsam değerleri) bellekte toplar.</summary>
public sealed class CapturingLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private readonly ConcurrentQueue<string> _lines = new();
    private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();

    public IReadOnlyCollection<string> Lines => _lines;
    public string All => string.Join(Environment.NewLine, _lines);

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(this, categoryName);
    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;
    public void Dispose() { }

    private sealed class CapturingLogger(CapturingLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => owner._scopes.Push(state);
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var scopeText = new System.Text.StringBuilder();
            owner._scopes.ForEachScope((scope, builder) =>
            {
                if (scope is IEnumerable<KeyValuePair<string, object>> pairs)
                    foreach (var (key, value) in pairs) builder.Append(' ').Append(key).Append('=').Append(value);
                else builder.Append(' ').Append(scope);
            }, scopeText);
            owner._lines.Enqueue($"{logLevel} {category}: {formatter(state, exception)} {exception}{scopeText}");
        }
    }
}
