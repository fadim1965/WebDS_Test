namespace WebDS_Test.Data;

/// <summary>
/// Guarantees that the transactional email outbox table exists in a store created before it was
/// modelled.
/// </summary>
/// <remarks>
/// <para>
/// Exactly the problem <see cref="ReviewSchema"/> and <see cref="LogTable"/> solve:
/// <c>EnsureCreated</c> builds the whole schema only when the database is empty, so it cannot add
/// <c>EmailMessages</c> to the deployed <c>MVStores</c> store (or the SQLite development file).
/// The table is created on demand with guarded DDL instead.
/// </para>
/// <para>
/// The statements are what EF Core generates for <see cref="Domain.EmailMessage"/> — column names,
/// lengths and nullability match the model, so a database patched here is indistinguishable at run
/// time from one built by <c>EnsureCreated</c>. Both dialects are guarded (<c>IF … IS NULL</c>,
/// <c>IF NOT EXISTS</c>), so running them on every start is a no-op once the table is present. They
/// carry no parameters and no user input.
/// </para>
/// </remarks>
public static class EmailSchema
{
    private const string SqlServerDdl = """
        IF OBJECT_ID(N'[dbo].[EmailMessages]', N'U') IS NULL
        BEGIN
            CREATE TABLE [dbo].[EmailMessages] (
                [Id] int NOT NULL IDENTITY,
                [ToAddress] nvarchar(256) NOT NULL,
                [ToName] nvarchar(120) NULL,
                [Subject] nvarchar(200) NOT NULL,
                [Kind] nvarchar(60) NOT NULL,
                [BodyHtml] nvarchar(max) NOT NULL,
                [BodyText] nvarchar(max) NOT NULL,
                [Status] int NOT NULL,
                [Attempts] int NOT NULL,
                [LastError] nvarchar(1000) NULL,
                [DedupeKey] nvarchar(120) NULL,
                [UserId] nvarchar(450) NULL,
                [OrderId] int NULL,
                [CreatedUtc] datetime2 NOT NULL,
                [SentUtc] datetime2 NULL,
                [NextAttemptUtc] datetime2 NULL,
                CONSTRAINT [PK_EmailMessages] PRIMARY KEY ([Id])
            );
            CREATE INDEX [IX_EmailMessages_Status] ON [dbo].[EmailMessages] ([Status]);
            CREATE INDEX [IX_EmailMessages_CreatedUtc] ON [dbo].[EmailMessages] ([CreatedUtc]);
            CREATE INDEX [IX_EmailMessages_Kind] ON [dbo].[EmailMessages] ([Kind]);
            CREATE INDEX [IX_EmailMessages_DedupeKey] ON [dbo].[EmailMessages] ([DedupeKey]);
            CREATE INDEX [IX_EmailMessages_UserId] ON [dbo].[EmailMessages] ([UserId]);
            CREATE INDEX [IX_EmailMessages_OrderId] ON [dbo].[EmailMessages] ([OrderId]);
        END
        """;

    private const string SqliteDdl = """
        CREATE TABLE IF NOT EXISTS "EmailMessages" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_EmailMessages" PRIMARY KEY AUTOINCREMENT,
            "ToAddress" TEXT NOT NULL,
            "ToName" TEXT NULL,
            "Subject" TEXT NOT NULL,
            "Kind" TEXT NOT NULL,
            "BodyHtml" TEXT NOT NULL,
            "BodyText" TEXT NOT NULL,
            "Status" INTEGER NOT NULL,
            "Attempts" INTEGER NOT NULL,
            "LastError" TEXT NULL,
            "DedupeKey" TEXT NULL,
            "UserId" TEXT NULL,
            "OrderId" INTEGER NULL,
            "CreatedUtc" TEXT NOT NULL,
            "SentUtc" TEXT NULL,
            "NextAttemptUtc" TEXT NULL
        );
        CREATE INDEX IF NOT EXISTS "IX_EmailMessages_Status" ON "EmailMessages" ("Status");
        CREATE INDEX IF NOT EXISTS "IX_EmailMessages_CreatedUtc" ON "EmailMessages" ("CreatedUtc");
        CREATE INDEX IF NOT EXISTS "IX_EmailMessages_Kind" ON "EmailMessages" ("Kind");
        CREATE INDEX IF NOT EXISTS "IX_EmailMessages_DedupeKey" ON "EmailMessages" ("DedupeKey");
        CREATE INDEX IF NOT EXISTS "IX_EmailMessages_UserId" ON "EmailMessages" ("UserId");
        CREATE INDEX IF NOT EXISTS "IX_EmailMessages_OrderId" ON "EmailMessages" ("OrderId");
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
