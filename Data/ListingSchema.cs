using Microsoft.EntityFrameworkCore;

namespace WebDS_Test.Data;

/// <summary>
/// Guarantees the listing-moderation columns exist on <c>Products</c> in a store created before
/// listings were reviewed.
/// </summary>
/// <remarks>
/// <para>
/// The same problem <see cref="VendorSchema"/> and <see cref="PayoutStatusSchema"/> solve, for a
/// table that already exists: <c>EnsureCreated</c> builds the whole schema only when the database
/// is empty, so it cannot add a column to the deployed <c>MVStores</c> store or the SQLite
/// development file.
/// </para>
/// <para>
/// The new column carries <c>DEFAULT 0</c>, which is <see cref="Domain.ListingStatus.Published"/> —
/// the state every listing had before moderation existed. A store that predates the feature
/// therefore keeps every listing on sale, and the marketplace only moderates listings submitted
/// after the patch is applied. The statements are static and carry no parameters and no user input.
/// </para>
/// </remarks>
public static class ListingSchema
{
    private const string SqlServerDdl = """
        IF COL_LENGTH(N'[dbo].[Products]', N'ModerationStatus') IS NULL
        BEGIN
            ALTER TABLE [dbo].[Products] ADD [ModerationStatus] int NOT NULL DEFAULT 0;
            CREATE INDEX [IX_Products_ModerationStatus] ON [dbo].[Products] ([ModerationStatus]);
        END

        IF COL_LENGTH(N'[dbo].[Products]', N'ModerationReason') IS NULL
        BEGIN
            ALTER TABLE [dbo].[Products] ADD [ModerationReason] nvarchar(500) NULL;
        END

        IF COL_LENGTH(N'[dbo].[Products]', N'ModeratedUtc') IS NULL
        BEGIN
            ALTER TABLE [dbo].[Products] ADD [ModeratedUtc] datetime2 NULL;
        END
        """;

    private const string SqliteDdl = """
        CREATE INDEX IF NOT EXISTS "IX_Products_ModerationStatus" ON "Products" ("ModerationStatus");
        """;

    public static async Task EnsureAsync(ApplicationDbContext db, CancellationToken ct = default)
    {
        if (db.Database.IsSqlServer())
        {
            await db.Database.ExecuteSqlRawAsync(SqlServerDdl, ct);
        }
        else if (db.Database.IsSqlite())
        {
            // SQLite has no guarded form of ALTER TABLE ADD COLUMN, so the columns are added
            // through the CouponSchema probe and the index is created separately.
            await CouponSchema.AddSqliteColumnIfMissingAsync(
                db, "Products", "ModerationStatus", "INTEGER NOT NULL DEFAULT 0", ct);
            await CouponSchema.AddSqliteColumnIfMissingAsync(
                db, "Products", "ModerationReason", "TEXT NULL", ct);
            await CouponSchema.AddSqliteColumnIfMissingAsync(
                db, "Products", "ModeratedUtc", "TEXT NULL", ct);

            await db.Database.ExecuteSqlRawAsync(SqliteDdl, ct);
        }
    }
}
