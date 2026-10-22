using Microsoft.EntityFrameworkCore;

namespace WebDS_Test.Data;

/// <summary>
/// Guarantees the translations table exists on a store created before strings were editable.
/// </summary>
/// <remarks>
/// <para>
/// Same problem as <see cref="SettingsSchema"/> and the other guards: <c>EnsureCreated</c> builds
/// the whole schema only when the database is empty, so a store that predates <c>Translations</c>
/// needs the table added on demand — before the seeder reads the seed catalogue into it or the
/// translator queries it.
/// </para>
/// <para>
/// The create statements match what EF Core generates for <see cref="Domain.Translation"/>:
/// column names, lengths, nullability, and the unique index over <c>(Culture, Key)</c> that makes a
/// duplicate string impossible. SQLite has no guarded form of <c>CREATE INDEX</c>, so its
/// statements carry the same <c>IF NOT EXISTS</c> guards the other patches use. The statements are
/// static and carry no parameters and no user input.
/// </para>
/// </remarks>
public static class TranslationSchema
{
    private const string SqlServerDdl = """
        IF OBJECT_ID(N'[dbo].[Translations]', N'U') IS NULL
        BEGIN
            CREATE TABLE [dbo].[Translations] (
                [Id] int NOT NULL IDENTITY,
                [Culture] nvarchar(8) NOT NULL,
                [Key] nvarchar(128) NOT NULL,
                [Value] nvarchar(2000) NOT NULL,
                [UpdatedUtc] datetime2 NOT NULL,
                [UpdatedBy] nvarchar(128) NULL,
                CONSTRAINT [PK_Translations] PRIMARY KEY ([Id])
            );
            CREATE UNIQUE INDEX [IX_Translations_Culture_Key] ON [dbo].[Translations] ([Culture], [Key]);
        END
        """;

    private const string SqliteDdl = """
        CREATE TABLE IF NOT EXISTS "Translations" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_Translations" PRIMARY KEY AUTOINCREMENT,
            "Culture" TEXT NOT NULL,
            "Key" TEXT NOT NULL,
            "Value" TEXT NOT NULL,
            "UpdatedUtc" TEXT NOT NULL,
            "UpdatedBy" TEXT NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_Translations_Culture_Key" ON "Translations" ("Culture", "Key");
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
