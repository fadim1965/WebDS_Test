namespace WebDS_Test.Data;

/// <summary>
/// Guarantees that the <c>Log</c> table the database log sink writes to exists.
/// </summary>
/// <remarks>
/// <para>
/// <c>EnsureCreated</c> builds the whole schema only when the database is empty, so it cannot add
/// <c>Log</c> to a store that already exists — which is exactly the case for the SQLite development
/// file, created before the sink. Rather than let logging quietly stop working there, the table is
/// created on demand.
/// </para>
/// <para>
/// Both statements are the DDL EF Core generates for <see cref="LogEntry"/> and both are guarded
/// (<c>IF … IS NULL</c>, <c>IF NOT EXISTS</c>), so running them on every start is a no-op once the
/// table is there. They carry no parameters and no user input, which is why this is the one raw
/// SQL statement in the project.
/// </para>
/// </remarks>
public static class LogTable
{
    private const string SqlServerDdl = """
        IF OBJECT_ID(N'[dbo].[Log]', N'U') IS NULL
        BEGIN
            CREATE TABLE [dbo].[Log] (
                [Id] bigint NOT NULL IDENTITY,
                [Application] nvarchar(100) NOT NULL,
                [Logged] datetime2 NOT NULL,
                [Level] nvarchar(100) NOT NULL,
                [Message] nvarchar(max) NOT NULL,
                [Logger] nvarchar(500) NULL,
                [Callsite] nvarchar(max) NULL,
                [Exception] nvarchar(max) NULL,
                CONSTRAINT [PK_Log] PRIMARY KEY ([Id])
            );
            CREATE INDEX [IX_Log_Logged] ON [dbo].[Log] ([Logged]);
        END
        """;

    private const string SqliteDdl = """
        CREATE TABLE IF NOT EXISTS "Log" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_Log" PRIMARY KEY AUTOINCREMENT,
            "Application" TEXT NOT NULL,
            "Logged" TEXT NOT NULL,
            "Level" TEXT NOT NULL,
            "Message" TEXT NOT NULL,
            "Logger" TEXT NULL,
            "Callsite" TEXT NULL,
            "Exception" TEXT NULL
        );
        CREATE INDEX IF NOT EXISTS "IX_Log_Logged" ON "Log" ("Logged")
        """;

    public static async Task EnsureAsync(ApplicationDbContext db, CancellationToken ct = default)
    {
        if (db.Database.IsSqlServer())
        {
            await db.Database.ExecuteSqlRawAsync(SqlServerDdl, ct);
        }
        else if (db.Database.IsSqlite())
        {
            await db.Database.ExecuteSqlRawAsync(SqliteDdl, ct);
        }
    }
}
