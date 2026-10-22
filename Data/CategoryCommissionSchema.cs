using Microsoft.EntityFrameworkCore;
using WebDS_Test.Data;

namespace WebDS_Test.Data;

/// <summary>
/// Guarantees the category commission column exists in a store created before per-category
/// commission was modelled.
/// </summary>
/// <remarks>
/// The same problem <see cref="VendorSchema"/> and <see cref="ListingSchema"/> solve, for a table
/// that already exists: <c>EnsureCreated</c> builds the whole schema only when the database is
/// empty, so it cannot add a column to the deployed <c>MVStores</c> store or the SQLite
/// development file.
/// </remarks>
public static class CategoryCommissionSchema
{
    private const string SqlServerDdl = """
        IF COL_LENGTH(N'[dbo].[Categories]', N'CommissionRate') IS NULL
        BEGIN
            ALTER TABLE [dbo].[Categories] ADD [CommissionRate] decimal(5,4) NULL;
        END
        """;

    private const string SqliteDdl = """
        -- SQLite: no action needed, column will be added by CouponSchema probe
        """;

    public static async Task EnsureAsync(ApplicationDbContext db, CancellationToken ct = default)
    {
        if (db.Database.IsSqlServer())
        {
            await db.Database.ExecuteSqlRawAsync(SqlServerDdl, ct);
        }
        else if (db.Database.IsSqlite())
        {
            await CouponSchema.AddSqliteColumnIfMissingAsync(
                db, "Categories", "CommissionRate", "REAL NULL", ct);
        }
    }
}