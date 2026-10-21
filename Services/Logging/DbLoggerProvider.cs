namespace WebDS_Test.Services.Logging;

/// <summary>
/// Writes everything the application logs to the store's <c>Log</c> table.
/// </summary>
/// <remarks>
/// <para>
/// A plain <see cref="ILoggerProvider"/>, so it composes with the console and debug providers
/// instead of replacing them: the console keeps showing the running log, the table keeps a trail
/// that outlives the process. Filtering is the standard one — <c>Logging:LogLevel</c> applies, and
/// <c>Logging:Database:LogLevel</c> singles this sink out.
/// </para>
/// <para>
/// Formatting happens on the caller's thread; the database write happens on
/// <see cref="DbLogWriter"/>. Writing to the table is never logged itself: the sink suppresses its
/// own output while it writes, so a database failure cannot feed itself. That matters because EF
/// logs the SQL it executes at <c>Information</c> in development — without the guard, every insert
/// into <c>Log</c> would queue another one.
/// </para>
/// </remarks>
[ProviderAlias(Alias)]
public sealed class DbLoggerProvider : ILoggerProvider
{
    /// <summary>Names this sink's configuration: <c>Logging:Database</c>.</summary>
    public const string Alias = "Database";

    // Set on the async flow that is writing to the table, so the SQL statements that insert the
    // rows are not themselves queued for insertion.
    private static readonly AsyncLocal<bool> Writing = new();

    private readonly DbLogQueue _queue;

    public DbLoggerProvider(DbLogQueue queue)
    {
        _queue = queue;
    }

    /// <summary>True while the sink is writing to the database.</summary>
    public static bool IsWriting => Writing.Value;

    /// <summary>
    /// Marks the current async flow as "this is the sink writing to the table", so anything logged
    /// during the write — EF's command logging above all — is dropped instead of queued.
    /// </summary>
    public static IDisposable Suppress()
    {
        var previous = Writing.Value;
        Writing.Value = true;
        return new Suppression(previous);
    }

    public ILogger CreateLogger(string categoryName) => new DbLogger(_queue, categoryName);

    public void Dispose()
    {
        // Nothing to release: the queue outlives this provider and the writer drains it.
    }

    private sealed class Suppression(bool previous) : IDisposable
    {
        private bool _restored;

        public void Dispose()
        {
            if (_restored)
            {
                return;
            }

            _restored = true;
            Writing.Value = previous;
        }
    }

    private sealed class DbLogger(DbLogQueue queue, string category) : ILogger
    {
        // Scopes carry structured values that this table has no column for, so there is nothing to
        // keep and nothing to unwind.
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        // The factory has already applied the configured rules by the time this is reached.
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.None || IsWriting)
            {
                return;
            }

            var message = formatter(state, exception);
            if (string.IsNullOrEmpty(message))
            {
                // Message is NOT NULL, and an event that carries only an exception still has to say
                // something in the message column.
                message = exception is null ? string.Empty : $"{exception.GetType().Name}: {exception.Message}";
            }

            queue.TryEnqueue(new DbLogRecord(
                DateTime.UtcNow,
                logLevel.ToString(),
                message,
                category,
                OriginalTemplate(state, message),
                exception?.ToString()));
        }

        /// <summary>
        /// The caller's message template — "User {UserId} signed in" for "User 42 signed in" — or
        /// null when there is none, or when it says no more than the rendered message already does.
        /// </summary>
        private static string? OriginalTemplate<TState>(TState state, string message)
        {
            if (state is not IEnumerable<KeyValuePair<string, object?>> values)
            {
                return null;
            }

            foreach (var pair in values)
            {
                if (pair.Key == "{OriginalFormat}" && pair.Value is string template && template != message)
                {
                    return template;
                }
            }

            return null;
        }
    }
}
