using System.Threading.Channels;
using CellWatcher.Models;
using Microsoft.Extensions.Logging;

namespace CellWatcher.Services;

// Buffers Error/Critical log entries from anywhere in the app so ApplicationErrorSinkService
// can persist them without the logging call site ever blocking on a database write. Bounded
// and drop-oldest: a burst of errors during a DB outage must not build unbounded memory or
// block the threads that are busy failing.
public static class ApplicationErrorChannel
{
    public static readonly Channel<ApplicationErrorRecord> Instance = Channel.CreateBounded<ApplicationErrorRecord>(
        new BoundedChannelOptions(500) { FullMode = BoundedChannelFullMode.DropOldest });
}

public sealed class DatabaseErrorLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new DatabaseErrorLogger(categoryName);

    public void Dispose()
    {
    }
}

public sealed class DatabaseErrorLogger(string category) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        // Never let this provider's own failure to enqueue become a logged error — that
        // would recurse back into here. TryWrite doesn't throw, so nothing to guard beyond
        // the enabled check.
        if (!IsEnabled(logLevel))
            return;

        var record = new ApplicationErrorRecord(
            null,
            DateTime.Now,
            category,
            formatter(state, exception),
            exception?.GetType().FullName,
            exception?.ToString());

        ApplicationErrorChannel.Instance.Writer.TryWrite(record);
    }
}
