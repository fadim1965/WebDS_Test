using Microsoft.EntityFrameworkCore;

namespace WebDS_Test.Data;

/// <summary>
/// Guarantees the returns tables exist — and that the columns they hang off existing records do.
/// </summary>
/// <remarks>
/// <para>
/// The same problem <see cref="PaymentSchema"/>, <see cref="CouponSchema"/> and
/// <see cref="VendorSchema"/> solve: <c>EnsureCreated</c> builds the whole schema only when the
/// database is empty, so a store created before returns were modelled needs the two tables added on
/// demand — plus <c>VendorLedgerEntries.ReturnRequestId</c>, which a partial reversal is idempotent
/// on, and <c>Orders.RefundedTotal</c>, which every refund path adds to.
/// </para>
/// <para>
/// The create statements match what EF Core generates for <see cref="Domain.ReturnRequest"/> and
/// <see cref="Domain.ReturnLine"/> — column names, lengths and nullability match the model — so a
/// database patched here is indistinguishable at run time from one built by <c>EnsureCreated</c>.
/// SQL Server guards the columns with <c>COL_LENGTH</c>; SQLite has no <c>IF NOT EXISTS</c> form of
/// <c>ALTER TABLE ADD COLUMN</c>, so its columns are checked through <c>PRAGMA table_info</c> —
/// see <see cref="CouponSchema"/> for the same approach. Like the coupon patch, the SQLite column
/// additions carry no foreign key: SQLite cannot add a constraint to an existing table, and the
/// model, not the dialect, is what the application queries through.
/// </para>
/// <para>
/// The statements are static and carry no parameters and no user input.
/// </para>
/// </remarks>
public static class ReturnsSchema
{
    private const string SqlServerDdl = """
        IF OBJECT_ID(N'[dbo].[ReturnRequests]', N'U') IS NULL
        BEGIN
            CREATE TABLE [dbo].[ReturnRequests] (
                [Id] int NOT NULL IDENTITY,
                [OrderId] int NOT NULL,
                [ShopId] int NOT NULL,
                [Reference] nvarchar(32) NOT NULL,
                [Status] int NOT NULL,
                [UserId] nvarchar(450) NULL,
                [Email] nvarchar(256) NOT NULL,
                [CustomerNote] nvarchar(1000) NULL,
                [DecisionNote] nvarchar(1000) NULL,
                [HandledBy] nvarchar(256) NULL,
                [RefundAmount] decimal(18,2) NOT NULL,
                [RequestedUtc] datetime2 NOT NULL,
                [DecidedUtc] datetime2 NULL,
                [ReceivedUtc] datetime2 NULL,
                [RefundedUtc] datetime2 NULL,
                CONSTRAINT [PK_ReturnRequests] PRIMARY KEY ([Id]),
                CONSTRAINT [FK_ReturnRequests_Orders_OrderId] FOREIGN KEY ([OrderId])
                    REFERENCES [dbo].[Orders] ([Id]),
                CONSTRAINT [FK_ReturnRequests_Shops_ShopId] FOREIGN KEY ([ShopId])
                    REFERENCES [dbo].[Shops] ([Id]),
                CONSTRAINT [FK_ReturnRequests_Users_UserId] FOREIGN KEY ([UserId])
                    REFERENCES [dbo].[Users] ([Id]) ON DELETE SET NULL
            );
            CREATE UNIQUE INDEX [IX_ReturnRequests_Reference] ON [dbo].[ReturnRequests] ([Reference]);
            CREATE INDEX [IX_ReturnRequests_OrderId] ON [dbo].[ReturnRequests] ([OrderId]);
            CREATE INDEX [IX_ReturnRequests_ShopId] ON [dbo].[ReturnRequests] ([ShopId]);
            CREATE INDEX [IX_ReturnRequests_Status] ON [dbo].[ReturnRequests] ([Status]);
            CREATE INDEX [IX_ReturnRequests_UserId] ON [dbo].[ReturnRequests] ([UserId]);
        END

        IF OBJECT_ID(N'[dbo].[ReturnLines]', N'U') IS NULL
        BEGIN
            CREATE TABLE [dbo].[ReturnLines] (
                [Id] int NOT NULL IDENTITY,
                [ReturnRequestId] int NOT NULL,
                [OrderLineId] int NOT NULL,
                [Quantity] int NOT NULL,
                [UnitPrice] decimal(18,2) NOT NULL,
                CONSTRAINT [PK_ReturnLines] PRIMARY KEY ([Id]),
                CONSTRAINT [FK_ReturnLines_ReturnRequests_ReturnRequestId] FOREIGN KEY ([ReturnRequestId])
                    REFERENCES [dbo].[ReturnRequests] ([Id]) ON DELETE CASCADE,
                CONSTRAINT [FK_ReturnLines_OrderLines_OrderLineId] FOREIGN KEY ([OrderLineId])
                    REFERENCES [dbo].[OrderLines] ([Id])
            );
            CREATE INDEX [IX_ReturnLines_ReturnRequestId] ON [dbo].[ReturnLines] ([ReturnRequestId]);
            CREATE INDEX [IX_ReturnLines_OrderLineId] ON [dbo].[ReturnLines] ([OrderLineId]);
        END

        IF COL_LENGTH(N'[dbo].[ReturnRequests]', N'RefundAmount') IS NULL
        BEGIN
            ALTER TABLE [dbo].[ReturnRequests] ADD [RefundAmount] decimal(18,2) NOT NULL DEFAULT 0;
        END

        IF COL_LENGTH(N'[dbo].[ReturnRequests]', N'RequestedUtc') IS NULL
        BEGIN
            -- An existing table receiving the timestamps back-dates its rows to the patch moment;
            -- there are no rows worth preserving in a demo store, so this is only cosmetic.
            ALTER TABLE [dbo].[ReturnRequests] ADD [RequestedUtc] datetime2 NOT NULL DEFAULT GETUTCDATE();
        END

        IF COL_LENGTH(N'[dbo].[ReturnRequests]', N'DecidedUtc') IS NULL
        BEGIN
            ALTER TABLE [dbo].[ReturnRequests] ADD [DecidedUtc] datetime2 NULL;
        END

        IF COL_LENGTH(N'[dbo].[ReturnRequests]', N'ReceivedUtc') IS NULL
        BEGIN
            ALTER TABLE [dbo].[ReturnRequests] ADD [ReceivedUtc] datetime2 NULL;
        END

        IF COL_LENGTH(N'[dbo].[ReturnRequests]', N'RefundedUtc') IS NULL
        BEGIN
            ALTER TABLE [dbo].[ReturnRequests] ADD [RefundedUtc] datetime2 NULL;
        END

        IF COL_LENGTH(N'[dbo].[VendorLedgerEntries]', N'ReturnRequestId') IS NULL
        BEGIN
            ALTER TABLE [dbo].[VendorLedgerEntries] ADD [ReturnRequestId] int NULL;
            CREATE INDEX [IX_VendorLedgerEntries_ReturnRequestId] ON [dbo].[VendorLedgerEntries] ([ReturnRequestId]);
            ALTER TABLE [dbo].[VendorLedgerEntries] ADD CONSTRAINT [FK_VendorLedgerEntries_ReturnRequests_ReturnRequestId]
                FOREIGN KEY ([ReturnRequestId]) REFERENCES [dbo].[ReturnRequests] ([Id]) ON DELETE SET NULL;
        END

        IF COL_LENGTH(N'[dbo].[VendorLedgerEntries]', N'PostSaleRequestId') IS NULL
        BEGIN
            ALTER TABLE [dbo].[VendorLedgerEntries] ADD [PostSaleRequestId] int NULL;
            CREATE INDEX [IX_VendorLedgerEntries_PostSaleRequestId] ON [dbo].[VendorLedgerEntries] ([PostSaleRequestId]);
            ALTER TABLE [dbo].[VendorLedgerEntries] ADD CONSTRAINT [FK_VendorLedgerEntries_PostSaleRequests_PostSaleRequestId]
                FOREIGN KEY ([PostSaleRequestId]) REFERENCES [dbo].[PostSaleRequests] ([Id]) ON DELETE SET NULL;
        END

        IF COL_LENGTH(N'[dbo].[Orders]', N'RefundedTotal') IS NULL
        BEGIN
            ALTER TABLE [dbo].[Orders] ADD [RefundedTotal] decimal(18,2) NOT NULL DEFAULT 0;
        END
        """;

    private const string SqliteDdl = """
        CREATE TABLE IF NOT EXISTS "ReturnRequests" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_ReturnRequests" PRIMARY KEY AUTOINCREMENT,
            "OrderId" INTEGER NOT NULL,
            "ShopId" INTEGER NOT NULL,
            "Reference" TEXT NOT NULL,
            "Status" INTEGER NOT NULL,
            "UserId" TEXT NULL,
            "Email" TEXT NOT NULL,
            "CustomerNote" TEXT NULL,
            "DecisionNote" TEXT NULL,
            "HandledBy" TEXT NULL,
            "RefundAmount" TEXT NOT NULL,
            "RequestedUtc" TEXT NOT NULL,
            "DecidedUtc" TEXT NULL,
            "ReceivedUtc" TEXT NULL,
            "RefundedUtc" TEXT NULL,
            CONSTRAINT "FK_ReturnRequests_Orders_OrderId" FOREIGN KEY ("OrderId")
                REFERENCES "Orders" ("Id") ON DELETE RESTRICT,
            CONSTRAINT "FK_ReturnRequests_Shops_ShopId" FOREIGN KEY ("ShopId")
                REFERENCES "Shops" ("Id") ON DELETE RESTRICT,
            CONSTRAINT "FK_ReturnRequests_Users_UserId" FOREIGN KEY ("UserId")
                REFERENCES "Users" ("Id") ON DELETE SET NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_ReturnRequests_Reference" ON "ReturnRequests" ("Reference");
        CREATE INDEX IF NOT EXISTS "IX_ReturnRequests_OrderId" ON "ReturnRequests" ("OrderId");
        CREATE INDEX IF NOT EXISTS "IX_ReturnRequests_ShopId" ON "ReturnRequests" ("ShopId");
        CREATE INDEX IF NOT EXISTS "IX_ReturnRequests_Status" ON "ReturnRequests" ("Status");
        CREATE INDEX IF NOT EXISTS "IX_ReturnRequests_UserId" ON "ReturnRequests" ("UserId");

        CREATE TABLE IF NOT EXISTS "ReturnLines" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_ReturnLines" PRIMARY KEY AUTOINCREMENT,
            "ReturnRequestId" INTEGER NOT NULL,
            "OrderLineId" INTEGER NOT NULL,
            "Quantity" INTEGER NOT NULL,
            "UnitPrice" TEXT NOT NULL,
            CONSTRAINT "FK_ReturnLines_ReturnRequests_ReturnRequestId" FOREIGN KEY ("ReturnRequestId")
                REFERENCES "ReturnRequests" ("Id") ON DELETE CASCADE,
            CONSTRAINT "FK_ReturnLines_OrderLines_OrderLineId" FOREIGN KEY ("OrderLineId")
                REFERENCES "OrderLines" ("Id") ON DELETE RESTRICT
        );
        CREATE INDEX IF NOT EXISTS "IX_ReturnLines_ReturnRequestId" ON "ReturnLines" ("ReturnRequestId");
        CREATE INDEX IF NOT EXISTS "IX_ReturnLines_OrderLineId" ON "ReturnLines" ("OrderLineId");
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
            await CouponSchema.AddSqliteColumnIfMissingAsync(
                db, "VendorLedgerEntries", "ReturnRequestId", "INTEGER NULL", ct);
            await CouponSchema.AddSqliteColumnIfMissingAsync(
                db, "VendorLedgerEntries", "PostSaleRequestId", "INTEGER NULL", ct);
            await CouponSchema.AddSqliteColumnIfMissingAsync(
                db, "Orders", "RefundedTotal", "TEXT NOT NULL DEFAULT 0", ct);
            await CouponSchema.AddSqliteColumnIfMissingAsync(
                db, "ReturnRequests", "RefundAmount", "TEXT NOT NULL DEFAULT 0", ct);
            await CouponSchema.AddSqliteColumnIfMissingAsync(
                db, "ReturnRequests", "RequestedUtc", "TEXT NOT NULL DEFAULT ''", ct);
            await CouponSchema.AddSqliteColumnIfMissingAsync(
                db, "ReturnRequests", "DecidedUtc", "TEXT NULL", ct);
            await CouponSchema.AddSqliteColumnIfMissingAsync(
                db, "ReturnRequests", "ReceivedUtc", "TEXT NULL", ct);
            await CouponSchema.AddSqliteColumnIfMissingAsync(
                db, "ReturnRequests", "RefundedUtc", "TEXT NULL", ct);
        }
    }
}
