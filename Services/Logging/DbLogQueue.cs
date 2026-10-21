using System.Threading.Channels;

namespace WebDS_Test.Services.Logging;

/// <summary>
/// A record on its way to the <c>Log</c> table: everything the writer needs, already formatted.
/// </summary>
public readonly record struct DbLogRecord(
    DateTime LoggedUtc,
    string Level,
    string Message,
    string Category,
    string? Template,
    string? Exception);

/// <summary>
/// The bounded hand-off between <see cref="ILogger"/> callers and <see cref="DbLogWriter"/>.
/// </summary>
/// <remarks>
/// Logging happens on request threads; writing to the database must not. A logging call therefore
/// only formats and enqueues — it cannot block on a slow database and it cannot throw because one
/// is unreachable — and a single background reader does the writing. The channel is bounded so a
/// burst cannot exhaust memory; when it is full the newest records are dropped and counted rather
/// than lost silently.
/// </remarks>
public sealed class DbLogQueue
{
    private readonly Channel<DbLogRecord> _channel;
    private long _dropped;

    public DbLogQueue(int capacity)
    {
        _channel = Channel.CreateBounded<DbLogRecord>(new BoundedChannelOptions(Math.Max(1, capacity))
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropWrite
        });
    }

    /// <summary>How many records have been dropped because the queue was full.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    public ChannelReader<DbLogRecord> Reader => _channel.Reader;

    public bool TryEnqueue(in DbLogRecord record)
    {
        if (_channel.Writer.TryWrite(record))
        {
            return true;
        }

        Interlocked.Increment(ref _dropped);
        return false;
    }
}
