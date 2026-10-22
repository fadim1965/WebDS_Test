using Microsoft.EntityFrameworkCore;

namespace WebDS_Test.Data;

/// <summary>
/// Guarantees the two-factor authentication columns exist on <c>Users</c> in a store created
/// before 2FA was modelled.
/// </summary>
/// <remarks>
/// The same problem <see cref="CategoryCommissionSchema"/> solves, for a table that already
/// exists: <c>EnsureCreated</c> builds the whole schema only when the database is empty, so it
/// cannot add columns to the deployed <c>MVStores</c> store or the SQLite development file.
/// </remarks>
public static class TwoFactorSchema
{
    private const string SqlServerDdl = """
        IF COL_LENGTH(N'[dbo].[Users]', N'TwoFactorEnabled') IS NULL
        BEGIN
            ALTER TABLE [dbo].[Users] ADD [TwoFactorEnabled] bit NOT NULL DEFAULT 0;
        END

        IF COL_LENGTH(N'[dbo].[Users]', N'TwoFactorSecret') IS NULL
        BEGIN
            ALTER TABLE [dbo].[Users] ADD [TwoFactorSecret] nvarchar(64) NULL;
        END

        IF COL_LENGTH(N'[dbo].[Users]', N'TwoFactorRecoveryCodes') IS NULL
        BEGIN
            ALTER TABLE [dbo].[Users] ADD [TwoFactorRecoveryCodes] nvarchar(max) NULL;
        END
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
                db, "Users", "TwoFactorEnabled", "INTEGER NOT NULL DEFAULT 0", ct);
            await CouponSchema.AddSqliteColumnIfMissingAsync(
                db, "Users", "TwoFactorSecret", "TEXT NULL", ct);
            await CouponSchema.AddSqliteColumnIfMissingAsync(
                db, "Users", "TwoFactorRecoveryCodes", "TEXT NULL", ct);
        }
    }
}