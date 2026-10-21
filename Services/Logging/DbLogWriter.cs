namespace WebDS_Test.Services.Logging;

/// <summary>
/// Drains <see cref="DbLogQueue"/> into the store's <c>Log</c> table.
/// </summary>
/// <remarks>
/// <para>
/// It runs as a hosted service, so it starts once the store's schema exists and the application is
/// about to serve. Records logged before that — the whole startup, including the database
/// bootstrap — wait in the queue and are written on the first flush.
/// </para>
/// <para>
/// A sink that can take the application down is worse than no sink, so this writer never throws and
/// never retries: a batch that cannot be written is dropped and the reason is reported on
/// <c>stderr</c>. <see cref="ILogger"/> is deliberately not used for those reports — the message
/// would be queued for the very table that just failed.
/// </para>
/// </remarks>
public sealed class DbLogWriter(
    DbLogQueue queue,
    IServiceScopeFactory scopeFactory,
    DbLogOptions options,
    ILogger<DbLogWriter> logger) : BackgroundService
{
    /// <summary>Work is capped per tick so the timer — and the pruning below — keeps ticking under load.</summary>
    private const int MaxBatchesPerTick = 20;

    private static readonly TimeSpan ReportInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan PruneInterval = TimeSpan.FromHours(24);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(3);

    private DateTime _lastReportUtc = DateTime.MinValue;
    private DateTime _lastPruneUtc = DateTime.MinValue;
    private long _reportedDrops;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var batchSize = Math.Max(1, options.BatchSize);
        var batch = new List<DbLogRecord>(batchSize);

        logger.LogInformation(
            "Database log sink active: writing to the Log table in batches of {BatchSize}, flushed every {FlushSeconds}s.",
            batchSize,
            Math.Max(1, options.FlushSeconds));

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, options.FlushSeconds)));

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                for (var written = 0; written < MaxBatchesPerTick; written++)
                {
                    Drain(batch, batchSize);
                    if (batch.Count == 0)
                    {
                        break;
                    }

                    await WriteAsync(batch, stoppingToken);
                }

                ReportDrops();
                await PruneAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // The host is shutting down; the final flush is below.
        }
        finally
        {
            // The last lines — "Application is shutting down" among them — are worth keeping. A
            // short timeout keeps an unreachable database from holding the shutdown up.
            await FlushAtShutdownAsync(batch, batchSize);
        }
    }

    /// <summary>Takes up to one batch's worth of records; later ticks take the rest.</summary>
    private void Drain(List<DbLogRecord> batch, int batchSize)
    {
        while (batch.Count < batchSize && queue.Reader.TryRead(out var record))
        {
            batch.Add(record);
        }
    }

    private async Task WriteAsync(List<DbLogRecord> batch, CancellationToken ct)
    {
        var count = batch.Count;

        try
        {
            // The rows must not be logged back into the sink as they are written (see
            // DbLoggerProvider.Suppress).
            using (DbLoggerProvider.Suppress())
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                db.Logs.AddRange(batch.Select(ToEntity));
                await db.SaveChangesAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down or out of time: drop rather than retry.
        }
        catch (Exception ex)
        {
            Report($"{count} log record(s) could not be written to the Log table", ex);
        }
        finally
        {
            batch.Clear();
        }
    }

    private async Task FlushAtShutdownAsync(List<DbLogRecord> batch, int batchSize)
    {
        try
        {
            using var timeout = new CancellationTokenSource(ShutdownTimeout);
            Drain(batch, batchSize);
            if (batch.Count > 0)
            {
                await WriteAsync(batch, timeout.Token);
            }
        }
        catch (Exception)
        {
            // Nothing further can be done: the process is going away.
        }
    }

    /// <summary>
    /// Deletes rows past <see cref="DbLogOptions.RetentionDays"/>. Off by default; when it is on it
    /// runs once a day rather than on every flush.
    /// </summary>
    private async Task PruneAsync(CancellationToken ct)
    {
        if (options.RetentionDays <= 0 || DateTime.UtcNow - _lastPruneUtc < PruneInterval)
        {
            return;
        }

        _lastPruneUtc = DateTime.UtcNow;

        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-options.RetentionDays);
            int removed;

            using (DbLoggerProvider.Suppress())
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                removed = await db.Logs.Where(l => l.Logged < cutoff).ExecuteDeleteAsync(ct);
            }

            if (removed > 0)
            {
                logger.LogInformation(
                    "Removed {Count} log row(s) older than {Days} day(s).",
                    removed,
                    options.RetentionDays);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            Report("Expired log rows could not be removed", ex);
        }
    }

    private void ReportDrops()
    {
        var dropped = queue.Dropped;
        if (dropped <= _reportedDrops)
        {
            return;
        }

        _reportedDrops = dropped;
        Console.Error.WriteLine(
            $"[Database log sink] {dropped} record(s) dropped: writing to the Log table is falling behind.");
    }

    /// <summary>
    /// Reports a failure on <c>stderr</c>, at most once a minute. Logging it through
    /// <see cref="ILogger"/> would queue it for the table that just failed.
    /// </summary>
    private void Report(string context, Exception exception)
    {
        if (DateTime.UtcNow - _lastReportUtc < ReportInterval)
        {
            return;
        }

        _lastReportUtc = DateTime.UtcNow;
        Console.Error.WriteLine($"[Database log sink] {context}: {exception.GetBaseException().Message}");
    }

    private LogEntry ToEntity(DbLogRecord record) => new()
    {
        Application = Truncate(
            string.IsNullOrWhiteSpace(options.Application) ? "WebDS_Test" : options.Application,
            100),
        Logged = record.LoggedUtc,
        Level = Truncate(record.Level, 100),
        Message = record.Message,
        Logger = Truncate(record.Category, 500),
        Callsite = record.Template,
        Exception = record.Exception
    };

    /// <summary>
    /// A value that does not fit its column is worth less than a row that exists, so over-long
    /// values are cut rather than allowed to fail the insert.
    /// </summary>
    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
