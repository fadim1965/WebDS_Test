using Microsoft.EntityFrameworkCore;

namespace WebDS_Test.Data;

/// <summary>
/// Guarantees that the coupon tables — and the two columns they add to <c>Orders</c> — exist in a
/// store that was created before coupons were modelled.
/// </summary>
/// <remarks>
/// <para>
/// The same problem <see cref="ReviewSchema"/>, <see cref="EmailSchema"/> and <see cref="LogTable"/>
/// solve: <c>EnsureCreated</c> builds the whole schema only when the database is empty, so it cannot
/// add <c>Coupons</c>/<c>CouponUsages</c> (or <c>Orders.CouponCode</c>/<c>Orders.CouponId</c>) to the
/// deployed <c>MVStores</c> store or the SQLite development file.
/// </para>
/// <para>
/// The create statements are exactly what EF Core generates for <see cref="Domain.Coupon"/> and
/// <see cref="Domain.CouponUsage"/>, and the column additions match the new members on
/// <see cref="Domain.Order"/>. SQL Server guards the columns with <c>COL_LENGTH</c>; SQLite has no
/// such predicate for <c>ALTER TABLE ADD COLUMN</c>, so the columns are checked through
/// <c>PRAGMA table_info</c> first.
/// </para>
/// </remarks>
public static class CouponSchema
{
    private const string SqlServerDdl = """
        IF OBJECT_ID(N'[dbo].[Coupons]', N'U') IS NULL
        BEGIN
            CREATE TABLE [dbo].[Coupons] (
                [Id] int NOT NULL IDENTITY,
                [Code] nvarchar(64) NOT NULL,
                [Description] nvarchar(256) NULL,
                [Type] int NOT NULL,
                [Value] decimal(18,2) NOT NULL,
                [MinSubtotal] decimal(18,2) NOT NULL,
                [MaxUses] int NULL,
                [MaxUsesPerUser] int NULL,
                [UsedCount] int NOT NULL,
                [StartsUtc] datetime2 NULL,
                [ExpiresUtc] datetime2 NULL,
                [IsActive] bit NOT NULL,
                [AppliesToAll] bit NOT NULL,
                [ShopId] int NULL,
                [CreatedUtc] datetime2 NOT NULL,
                CONSTRAINT [PK_Coupons] PRIMARY KEY ([Id]),
                CONSTRAINT [FK_Coupons_Shops_ShopId] FOREIGN KEY ([ShopId])
                    REFERENCES [dbo].[Shops] ([Id])
            );
            CREATE UNIQUE INDEX [IX_Coupons_Code] ON [dbo].[Coupons] ([Code]);
            CREATE INDEX [IX_Coupons_IsActive] ON [dbo].[Coupons] ([IsActive]);
            CREATE INDEX [IX_Coupons_ShopId] ON [dbo].[Coupons] ([ShopId]);
        END

        IF OBJECT_ID(N'[dbo].[CouponUsages]', N'U') IS NULL
        BEGIN
            CREATE TABLE [dbo].[CouponUsages] (
                [Id] int NOT NULL IDENTITY,
                [CouponId] int NOT NULL,
                [UserId] nvarchar(450) NULL,
                [OrderId] int NULL,
                [RedeemedUtc] datetime2 NOT NULL,
                CONSTRAINT [PK_CouponUsages] PRIMARY KEY ([Id]),
                CONSTRAINT [FK_CouponUsages_Coupons_CouponId] FOREIGN KEY ([CouponId])
                    REFERENCES [dbo].[Coupons] ([Id]) ON DELETE CASCADE,
                CONSTRAINT [FK_CouponUsages_Users_UserId] FOREIGN KEY ([UserId])
                    REFERENCES [dbo].[Users] ([Id]) ON DELETE SET NULL,
                CONSTRAINT [FK_CouponUsages_Orders_OrderId] FOREIGN KEY ([OrderId])
                    REFERENCES [dbo].[Orders] ([Id]) ON DELETE SET NULL
            );
            CREATE INDEX [IX_CouponUsages_CouponId] ON [dbo].[CouponUsages] ([CouponId]);
            CREATE INDEX [IX_CouponUsages_UserId] ON [dbo].[CouponUsages] ([UserId]);
            CREATE INDEX [IX_CouponUsages_OrderId] ON [dbo].[CouponUsages] ([OrderId]);
        END

        IF COL_LENGTH(N'[dbo].[Orders]', N'CouponCode') IS NULL
        BEGIN
            ALTER TABLE [dbo].[Orders] ADD [CouponCode] nvarchar(64) NULL;
        END

        IF COL_LENGTH(N'[dbo].[Orders]', N'CouponId') IS NULL
        BEGIN
            ALTER TABLE [dbo].[Orders] ADD [CouponId] int NULL;
            ALTER TABLE [dbo].[Orders] ADD CONSTRAINT [FK_Orders_Coupons_CouponId]
                FOREIGN KEY ([CouponId]) REFERENCES [dbo].[Coupons] ([Id]) ON DELETE SET NULL;
            CREATE INDEX [IX_Orders_CouponId] ON [dbo].[Orders] ([CouponId]);
        END
        """;

    private const string SqliteDdl = """
        CREATE TABLE IF NOT EXISTS "Coupons" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_Coupons" PRIMARY KEY AUTOINCREMENT,
            "Code" TEXT NOT NULL,
            "Description" TEXT NULL,
            "Type" INTEGER NOT NULL,
            "Value" TEXT NOT NULL,
            "MinSubtotal" TEXT NOT NULL,
            "MaxUses" INTEGER NULL,
            "MaxUsesPerUser" INTEGER NULL,
            "UsedCount" INTEGER NOT NULL,
            "StartsUtc" TEXT NULL,
            "ExpiresUtc" TEXT NULL,
            "IsActive" INTEGER NOT NULL,
            "AppliesToAll" INTEGER NOT NULL,
            "ShopId" INTEGER NULL,
            "CreatedUtc" TEXT NOT NULL,
            CONSTRAINT "FK_Coupons_Shops_ShopId" FOREIGN KEY ("ShopId")
                REFERENCES "Shops" ("Id")
        );
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_Coupons_Code" ON "Coupons" ("Code");
        CREATE INDEX IF NOT EXISTS "IX_Coupons_IsActive" ON "Coupons" ("IsActive");
        CREATE INDEX IF NOT EXISTS "IX_Coupons_ShopId" ON "Coupons" ("ShopId");

        CREATE TABLE IF NOT EXISTS "CouponUsages" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_CouponUsages" PRIMARY KEY AUTOINCREMENT,
            "CouponId" INTEGER NOT NULL,
            "UserId" TEXT NULL,
            "OrderId" INTEGER NULL,
            "RedeemedUtc" TEXT NOT NULL,
            CONSTRAINT "FK_CouponUsages_Coupons_CouponId" FOREIGN KEY ("CouponId")
                REFERENCES "Coupons" ("Id") ON DELETE CASCADE,
            CONSTRAINT "FK_CouponUsages_Users_UserId" FOREIGN KEY ("UserId")
                REFERENCES "Users" ("Id") ON DELETE SET NULL,
            CONSTRAINT "FK_CouponUsages_Orders_OrderId" FOREIGN KEY ("OrderId")
                REFERENCES "Orders" ("Id") ON DELETE SET NULL
        );
        CREATE INDEX IF NOT EXISTS "IX_CouponUsages_CouponId" ON "CouponUsages" ("CouponId");
        CREATE INDEX IF NOT EXISTS "IX_CouponUsages_UserId" ON "CouponUsages" ("UserId");
        CREATE INDEX IF NOT EXISTS "IX_CouponUsages_OrderId" ON "CouponUsages" ("OrderId");
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
            await AddSqliteColumnIfMissingAsync(db, "Orders", "CouponCode", "TEXT NULL", ct);
            await AddSqliteColumnIfMissingAsync(db, "Orders", "CouponId", "INTEGER NULL", ct);
        }
    }

    /// <summary>
    /// Adds a column to a SQLite table when it is absent. <c>ALTER TABLE … ADD COLUMN</c> has no
    /// <c>IF NOT EXISTS</c> form, so existence is tested through <c>PRAGMA table_info</c> first.
    /// Internal so the later schema patches (<see cref="ReturnsSchema"/>) reuse it rather than
    /// shipping a second copy of the probe.
    /// </summary>
    internal static async Task AddSqliteColumnIfMissingAsync(
        ApplicationDbContext db, string table, string column, string definition, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        var wasOpen = connection.State == System.Data.ConnectionState.Open;

        if (!wasOpen)
        {
            await connection.OpenAsync(ct);
        }

        try
        {
            var exists = false;

            await using (var probe = connection.CreateCommand())
            {
                probe.CommandText = $"PRAGMA table_info(\"{table}\");";
                await using var reader = await probe.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                    {
                        exists = true;
                        break;
                    }
                }
            }

            if (!exists)
            {
                await using var alter = connection.CreateCommand();
                alter.CommandText = $"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {definition};";
                await alter.ExecuteNonQueryAsync(ct);
            }
        }
        finally
        {
            if (!wasOpen)
            {
                await connection.CloseAsync();
            }
        }
    }
}
