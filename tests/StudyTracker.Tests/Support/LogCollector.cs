using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace StudyTracker.Tests.Support;

/// <summary>Провайдер журнала, который складывает записи (текст и исключения) в память для проверок.</summary>
public sealed class LogCollector : ILoggerProvider
{
    private readonly ConcurrentQueue<string> records = new();

    public IReadOnlyCollection<string> Records => records;

    public ILogger CreateLogger(string categoryName) => new Collecting(categoryName, records);

    public void Dispose() { }

    private sealed class Collecting(string category, ConcurrentQueue<string> records) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            records.Enqueue($"{category}: {formatter(state, exception)} {exception}");
    }
}
