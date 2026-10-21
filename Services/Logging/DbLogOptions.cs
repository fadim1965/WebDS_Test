namespace WebDS_Test.Services.Logging;

/// <summary>
/// Settings for the database log sink, bound from the <c>Logging:Database</c> section.
/// </summary>
/// <remarks>
/// The same section is where the sink's own log levels live, because the sink declares
/// <c>Database</c> as its provider alias: <c>Logging:Database:LogLevel</c> therefore filters the
/// table without touching the console.
/// <code>
/// "Logging": {
///   "Database": {
///     "Enabled": true,
///     "LogLevel": { "Default": "Information", "Microsoft.AspNetCore": "Warning" }
///   }
/// }
/// </code>
/// </remarks>
public sealed class DbLogOptions
{
    public const string SectionName = "Logging:Database";

    /// <summary>
    /// Whether the sink is registered at all. Console and debug logging are unaffected: with this
    /// false the application logs exactly as it did before the table existed.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Written to every row's <c>Application</c> column, so one table can serve several apps.</summary>
    public string Application { get; set; } = "WebDS_Test";

    /// <summary>
    /// How many records may wait in memory for the writer. When the queue is full the newest
    /// records are dropped and counted — reported out of band — rather than blocking the caller or
    /// growing without limit.
    /// </summary>
    public int QueueCapacity { get; set; } = 10_000;

    /// <summary>Rows per database round-trip.</summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>How long a record may wait in the queue before the writer flushes what it has.</summary>
    public int FlushSeconds { get; set; } = 2;

    /// <summary>
    /// Rows older than this many days are deleted, at most once a day. <c>0</c> — the default —
    /// keeps everything.
    /// </summary>
    public int RetentionDays { get; set; }
}
