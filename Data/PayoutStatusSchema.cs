using Microsoft.EntityFrameworkCore;

namespace WebDS_Test.Data;

/// <summary>
/// Guarantees the payout-lifecycle columns exist on <c>VendorPayouts</c>, and the payout-destination
/// columns on <c>Shops</c>, in a store created before they were modelled.
/// </summary>
/// <remarks>
/// <para>
/// The payout columns postdate the payouts table itself: settlements were first recorded as a
/// completed fact, and only later gained the ability to be marked failed or reversed. Existing rows
/// become <see cref="Domain.PayoutStatus.Paid"/> (0) through the DEFAULT on the new column, which is
/// exactly what a recorded settlement already meant.
/// </para>
/// <para>
/// SQL Server guards each addition with <c>COL_LENGTH</c>; SQLite has no <c>IF NOT EXISTS</c> form
/// of <c>ALTER TABLE ADD COLUMN</c>, so its columns are added through the
/// <see cref="CouponSchema"/> probe. The SQLite additions carry no foreign key — SQLite cannot add
/// a constraint to an existing table, and the model, not the dialect, is what the application
/// queries through.
/// </para>
/// </remarks>
public static class PayoutStatusSchema
{
    private const string SqlServerDdl = """
        IF COL_LENGTH(N'[dbo].[VendorPayouts]', N'Status') IS NULL
        BEGIN
            ALTER TABLE [dbo].[VendorPayouts] ADD [Status] int NOT NULL DEFAULT 0;
            CREATE INDEX [IX_VendorPayouts_Status] ON [dbo].[VendorPayouts] ([Status]);
        END

        IF COL_LENGTH(N'[dbo].[VendorPayouts]', N'FailureReason') IS NULL
        BEGIN
            ALTER TABLE [dbo].[VendorPayouts] ADD [FailureReason] nvarchar(500) NULL;
        END

        IF COL_LENGTH(N'[dbo].[VendorPayouts]', N'HandledBy') IS NULL
        BEGIN
            ALTER TABLE [dbo].[VendorPayouts] ADD [HandledBy] nvarchar(256) NULL;
        END

        IF COL_LENGTH(N'[dbo].[VendorPayouts]', N'FailedUtc') IS NULL
        BEGIN
            ALTER TABLE [dbo].[VendorPayouts] ADD [FailedUtc] datetime2 NULL;
        END

        IF COL_LENGTH(N'[dbo].[VendorPayouts]', N'ReversedUtc') IS NULL
        BEGIN
            ALTER TABLE [dbo].[VendorPayouts] ADD [ReversedUtc] datetime2 NULL;
        END

        IF COL_LENGTH(N'[dbo].[Shops]', N'BankAccountName') IS NULL
        BEGIN
            ALTER TABLE [dbo].[Shops] ADD [BankAccountName] nvarchar(120) NULL;
        END

        IF COL_LENGTH(N'[dbo].[Shops]', N'BankName') IS NULL
        BEGIN
            ALTER TABLE [dbo].[Shops] ADD [BankName] nvarchar(120) NULL;
        END

        IF COL_LENGTH(N'[dbo].[Shops]', N'BankIban') IS NULL
        BEGIN
            ALTER TABLE [dbo].[Shops] ADD [BankIban] nvarchar(40) NULL;
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
                db, "VendorPayouts", "Status", "INTEGER NOT NULL DEFAULT 0", ct);
            await CouponSchema.AddSqliteColumnIfMissingAsync(
                db, "VendorPayouts", "FailureReason", "TEXT NULL", ct);
            await CouponSchema.AddSqliteColumnIfMissingAsync(
                db, "VendorPayouts", "HandledBy", "TEXT NULL", ct);
            await CouponSchema.AddSqliteColumnIfMissingAsync(
                db, "VendorPayouts", "FailedUtc", "TEXT NULL", ct);
            await CouponSchema.AddSqliteColumnIfMissingAsync(
                db, "VendorPayouts", "ReversedUtc", "TEXT NULL", ct);

            await CouponSchema.AddSqliteColumnIfMissingAsync(
                db, "Shops", "BankAccountName", "TEXT NULL", ct);
            await CouponSchema.AddSqliteColumnIfMissingAsync(
                db, "Shops", "BankName", "TEXT NULL", ct);
            await CouponSchema.AddSqliteColumnIfMissingAsync(
                db, "Shops", "BankIban", "TEXT NULL", ct);
        }
    }
}
