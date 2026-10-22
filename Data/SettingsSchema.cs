using Microsoft.EntityFrameworkCore;

namespace WebDS_Test.Data;

/// <summary>
/// Guarantees the site-settings table exists on a store created before runtime settings were modelled.
/// </summary>
/// <remarks>
/// <para>
/// Same problem as <see cref="ConversationSchema"/>, <see cref="ReturnsSchema"/> and friends:
/// <c>EnsureCreated</c> builds the whole schema only when the database is empty, so a store that
/// predates <c>SiteSettings</c> needs the table added on demand — before the admin settings screen
/// or the currency formatter tries to query it.
/// </para>
/// <para>
/// The create statements match what EF Core generates for <see cref="Domain.SiteSetting"/>:
/// column names, lengths, nullability, the unique index on <c>Key</c>, and no foreign keys.
/// SQLite has no guarded form of <c>CREATE INDEX</c>, so its statements carry the same
/// <c>IF NOT EXISTS</c> guards the other patches use. The statements are static and carry no
/// parameters and no user input.
/// </para>
/// </remarks>
public static class SettingsSchema
{
    private const string SqlServerDdl = """
        IF OBJECT_ID(N'[dbo].[SiteSettings]', N'U') IS NULL
        BEGIN
            CREATE TABLE [dbo].[SiteSettings] (
                [Id] int NOT NULL IDENTITY,
                [Key] nvarchar(64) NOT NULL,
                [Value] nvarchar(512) NOT NULL,
                [UpdatedUtc] datetime2 NOT NULL,
                [UpdatedBy] nvarchar(128) NULL,
                CONSTRAINT [PK_SiteSettings] PRIMARY KEY ([Id])
            );
            CREATE UNIQUE INDEX [IX_SiteSettings_Key] ON [dbo].[SiteSettings] ([Key]);
        END
        """;

    private const string SqliteDdl = """
        CREATE TABLE IF NOT EXISTS "SiteSettings" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_SiteSettings" PRIMARY KEY AUTOINCREMENT,
            "Key" TEXT NOT NULL,
            "Value" TEXT NOT NULL,
            "UpdatedUtc" TEXT NOT NULL,
            "UpdatedBy" TEXT NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_SiteSettings_Key" ON "SiteSettings" ("Key");
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
