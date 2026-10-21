using System.ComponentModel.DataAnnotations;

namespace WebDS_Test.Domain;

/// <summary>
/// One line of the application's diagnostic trail: what happened, at which level, logged by which
/// component, and with which exception.
/// </summary>
/// <remarks>
/// <para>
/// This sits deliberately outside the store's domain model: nothing references it and it references
/// nothing, so it never takes part in an order or a catalogue query. Rows are written by
/// <c>WebDS_Test.Services.Logging.DbLoggerProvider</c> and read by whoever is diagnosing a
/// deployment.
/// </para>
/// <para>
/// The column widths reproduce the <c>Log</c> table the SQL Server database already carries, so the
/// same entity maps onto SQLite unchanged — see <see cref="WebDS_Test.Data.LogTable"/>.
/// </para>
/// </remarks>
public class LogEntry
{
    public long Id { get; set; }

    /// <summary>Which application produced the line, so several deployments can share one table.</summary>
    [Required, StringLength(100)]
    public string Application { get; set; } = string.Empty;

    /// <summary>When the event happened, in UTC — as every other timestamp in this application.</summary>
    public DateTime Logged { get; set; }

    /// <summary>Trace, Debug, Information, Warning, Error or Critical.</summary>
    [Required, StringLength(100)]
    public string Level { get; set; } = string.Empty;

    /// <summary>The formatted message, with the values that were substituted into it.</summary>
    [Required]
    public string Message { get; set; } = string.Empty;

    /// <summary>The logger category that emitted the line: normally the class that logged it.</summary>
    [StringLength(500)]
    public string? Logger { get; set; }

    /// <summary>
    /// The caller's message template when it differs from the rendered message — "User {UserId}
    /// signed in" rather than "User 42 signed in" — which makes repeated events countable.
    /// </summary>
    public string? Callsite { get; set; }

    /// <summary>The full exception, stack trace included, when the event carried one.</summary>
    public string? Exception { get; set; }
}
