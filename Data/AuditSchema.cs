using Microsoft.EntityFrameworkCore;

namespace WebDS_Test.Data;

/// <summary>
/// Guarantees the admin audit trail exists in a store created before it was modelled.
/// </summary>
/// <remarks>
/// <para>
/// The same problem <see cref="PaymentSchema"/> and <see cref="VendorSchema"/> solve:
/// <c>EnsureCreated</c> builds the whole schema only when the database is empty, so the
/// <c>AuditLogs</c> table must be added on demand to the deployed <c>MVStores</c> store and the
/// SQLite development file.
/// </para>
/// <para>
/// The statements match what EF Core generates for <see cref="Domain.AuditLog"/> — column names,
/// lengths and nullability match the model — so a database patched here is indistinguishable at run
/// time from one built by <c>EnsureCreated</c>. Like the outbox, the audit table holds loose
/// references rather than foreign keys, so there is nothing to cascade. The statements carry no
/// parameters and no user input.
/// </para>
/// </remarks>
public static class AuditSchema
{
    private const string SqlServerDdl = """
        IF OBJECT_ID(N'[dbo].[AuditLogs]', N'U') IS NULL
        BEGIN
            CREATE TABLE [dbo].[AuditLogs] (
                [Id] int NOT NULL IDENTITY,
                [Actor] nvarchar(256) NOT NULL,
                [Action] nvarchar(80) NOT NULL,
                [EntityType] nvarchar(40) NOT NULL,
                [EntityId] int NULL,
                [Summary] nvarchar(500) NULL,
                [CreatedUtc] datetime2 NOT NULL,
                CONSTRAINT [PK_AuditLogs] PRIMARY KEY ([Id])
            );
            CREATE INDEX [IX_AuditLogs_CreatedUtc] ON [dbo].[AuditLogs] ([CreatedUtc]);
            CREATE INDEX [IX_AuditLogs_Action] ON [dbo].[AuditLogs] ([Action]);
            CREATE INDEX [IX_AuditLogs_Actor] ON [dbo].[AuditLogs] ([Actor]);
            CREATE INDEX [IX_AuditLogs_EntityType_EntityId] ON [dbo].[AuditLogs] ([EntityType], [EntityId]);
        END
        """;

    private const string SqliteDdl = """
        CREATE TABLE IF NOT EXISTS "AuditLogs" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_AuditLogs" PRIMARY KEY AUTOINCREMENT,
            "Actor" TEXT NOT NULL,
            "Action" TEXT NOT NULL,
            "EntityType" TEXT NOT NULL,
            "EntityId" INTEGER NULL,
            "Summary" TEXT NULL,
            "CreatedUtc" TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS "IX_AuditLogs_CreatedUtc" ON "AuditLogs" ("CreatedUtc");
        CREATE INDEX IF NOT EXISTS "IX_AuditLogs_Action" ON "AuditLogs" ("Action");
        CREATE INDEX IF NOT EXISTS "IX_AuditLogs_Actor" ON "AuditLogs" ("Actor");
        CREATE INDEX IF NOT EXISTS "IX_AuditLogs_EntityType_EntityId" ON "AuditLogs" ("EntityType", "EntityId");
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
