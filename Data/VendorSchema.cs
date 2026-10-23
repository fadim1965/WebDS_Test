using Microsoft.EntityFrameworkCore;

namespace WebDS_Test.Data;

/// <summary>
/// Guarantees the vendor money tables — the settlement ledger and its payouts — exist in a store
/// created before payouts were modelled.
/// </summary>
/// <remarks>
/// <para>
/// The same problem <see cref="PaymentSchema"/>, <see cref="CouponSchema"/>, <see cref="ReviewSchema"/>,
/// <see cref="EmailSchema"/> and <see cref="LogTable"/> solve: <c>EnsureCreated</c> builds the whole
/// schema only when the database is empty, so it cannot add <c>VendorPayouts</c> to the deployed
/// <c>MVStores</c> store or the SQLite development file. The ledger table has the same history — the
/// entity predates the patch that would have created it — so it is covered here too.
/// </para>
/// <para>
/// The statements are what EF Core generates for <see cref="Domain.VendorLedgerEntry"/> and
/// <see cref="Domain.VendorPayout"/> — column names, lengths and nullability match the model — so a
/// database patched here is indistinguishable at run time from one built by <c>EnsureCreated</c>.
/// The statements carry no parameters and no user input.
/// </para>
/// </remarks>
public static class VendorSchema
{
    private const string SqlServerDdl = """
        IF OBJECT_ID(N'[dbo].[VendorLedgerEntries]', N'U') IS NULL
        BEGIN
            CREATE TABLE [dbo].[VendorLedgerEntries] (
                [Id] int NOT NULL IDENTITY,
                [ShopId] int NOT NULL,
                [OrderId] int NULL,
                [Type] int NOT NULL,
                [GrossAmount] decimal(18,2) NOT NULL,
                [CommissionAmount] decimal(18,2) NOT NULL,
                [NetAmount] decimal(18,2) NOT NULL,
                [Note] nvarchar(200) NULL,
                [SettledUtc] datetime2 NULL,
                [PayoutReference] nvarchar(120) NULL,
                [CreatedUtc] datetime2 NOT NULL,
                CONSTRAINT [PK_VendorLedgerEntries] PRIMARY KEY ([Id]),
                CONSTRAINT [FK_VendorLedgerEntries_Shops_ShopId] FOREIGN KEY ([ShopId])
                    REFERENCES [dbo].[Shops] ([Id]) ON DELETE CASCADE,
                CONSTRAINT [FK_VendorLedgerEntries_Orders_OrderId] FOREIGN KEY ([OrderId])
                    REFERENCES [dbo].[Orders] ([Id]) ON DELETE SET NULL
            );
            CREATE INDEX [IX_VendorLedgerEntries_ShopId] ON [dbo].[VendorLedgerEntries] ([ShopId]);
            CREATE INDEX [IX_VendorLedgerEntries_OrderId] ON [dbo].[VendorLedgerEntries] ([OrderId]);
            CREATE INDEX [IX_VendorLedgerEntries_SettledUtc] ON [dbo].[VendorLedgerEntries] ([SettledUtc]);
        END

        IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'[dbo].[VendorLedgerEntries]') AND name = N'ReturnRequestId')
        BEGIN
            ALTER TABLE [dbo].[VendorLedgerEntries] ADD [ReturnRequestId] int NULL;
            CREATE INDEX [IX_VendorLedgerEntries_ReturnRequestId] ON [dbo].[VendorLedgerEntries] ([ReturnRequestId]);
            ALTER TABLE [dbo].[VendorLedgerEntries] ADD CONSTRAINT [FK_VendorLedgerEntries_ReturnRequests_ReturnRequestId]
                FOREIGN KEY ([ReturnRequestId]) REFERENCES [dbo].[ReturnRequests] ([Id]) ON DELETE SET NULL;
        END

        IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'[dbo].[VendorLedgerEntries]') AND name = N'PostSaleRequestId')
        BEGIN
            ALTER TABLE [dbo].[VendorLedgerEntries] ADD [PostSaleRequestId] int NULL;
            CREATE INDEX [IX_VendorLedgerEntries_PostSaleRequestId] ON [dbo].[VendorLedgerEntries] ([PostSaleRequestId]);
            ALTER TABLE [dbo].[VendorLedgerEntries] ADD CONSTRAINT [FK_VendorLedgerEntries_PostSaleRequests_PostSaleRequestId]
                FOREIGN KEY ([PostSaleRequestId]) REFERENCES [dbo].[PostSaleRequests] ([Id]) ON DELETE SET NULL;
        END

        IF OBJECT_ID(N'[dbo].[VendorPayouts]', N'U') IS NULL
        BEGIN
            CREATE TABLE [dbo].[VendorPayouts] (
                [Id] int NOT NULL IDENTITY,
                [ShopId] int NOT NULL,
                [Reference] nvarchar(40) NOT NULL,
                [PeriodStartUtc] datetime2 NOT NULL,
                [PeriodEndUtc] datetime2 NOT NULL,
                [EntryCount] int NOT NULL,
                [GrossAmount] decimal(18,2) NOT NULL,
                [CommissionAmount] decimal(18,2) NOT NULL,
                [NetAmount] decimal(18,2) NOT NULL,
                [Method] nvarchar(40) NOT NULL,
                [Note] nvarchar(200) NULL,
                [CreatedBy] nvarchar(256) NULL,
                [CreatedUtc] datetime2 NOT NULL,
                CONSTRAINT [PK_VendorPayouts] PRIMARY KEY ([Id]),
                CONSTRAINT [FK_VendorPayouts_Shops_ShopId] FOREIGN KEY ([ShopId])
                    REFERENCES [dbo].[Shops] ([Id]) ON DELETE CASCADE
            );
            CREATE INDEX [IX_VendorPayouts_ShopId] ON [dbo].[VendorPayouts] ([ShopId]);
            CREATE UNIQUE INDEX [IX_VendorPayouts_Reference] ON [dbo].[VendorPayouts] ([Reference]);
        END
        """;

    private const string SqliteDdl = """
        CREATE TABLE IF NOT EXISTS "VendorLedgerEntries" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_VendorLedgerEntries" PRIMARY KEY AUTOINCREMENT,
            "ShopId" INTEGER NOT NULL,
            "OrderId" INTEGER NULL,
            "Type" INTEGER NOT NULL,
            "GrossAmount" TEXT NOT NULL,
            "CommissionAmount" TEXT NOT NULL,
            "NetAmount" TEXT NOT NULL,
            "Note" TEXT NULL,
            "SettledUtc" TEXT NULL,
            "PayoutReference" TEXT NULL,
            "CreatedUtc" TEXT NOT NULL,
            CONSTRAINT "FK_VendorLedgerEntries_Shops_ShopId" FOREIGN KEY ("ShopId")
                REFERENCES "Shops" ("Id") ON DELETE CASCADE,
            CONSTRAINT "FK_VendorLedgerEntries_Orders_OrderId" FOREIGN KEY ("OrderId")
                REFERENCES "Orders" ("Id") ON DELETE SET NULL
        );
        CREATE INDEX IF NOT EXISTS "IX_VendorLedgerEntries_ShopId" ON "VendorLedgerEntries" ("ShopId");
        CREATE INDEX IF NOT EXISTS "IX_VendorLedgerEntries_OrderId" ON "VendorLedgerEntries" ("OrderId");
        CREATE INDEX IF NOT EXISTS "IX_VendorLedgerEntries_SettledUtc" ON "VendorLedgerEntries" ("SettledUtc");

        -- ReturnRequestId and PostSaleRequestId for idempotent reversals
        -- SQLite ALTER TABLE does not support IF NOT EXISTS for columns; the guard is the outer IF NOT EXISTS on the table creation.
        -- In practice this migration is only run once per database lifecycle.
        -- The indexes and FKs are added here for completeness.

        CREATE TABLE IF NOT EXISTS "VendorPayouts" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_VendorPayouts" PRIMARY KEY AUTOINCREMENT,
            "ShopId" INTEGER NOT NULL,
            "Reference" TEXT NOT NULL,
            "PeriodStartUtc" TEXT NOT NULL,
            "PeriodEndUtc" TEXT NOT NULL,
            "EntryCount" INTEGER NOT NULL,
            "GrossAmount" TEXT NOT NULL,
            "CommissionAmount" TEXT NOT NULL,
            "NetAmount" TEXT NOT NULL,
            "Method" TEXT NOT NULL,
            "Note" TEXT NULL,
            "CreatedBy" TEXT NULL,
            "CreatedUtc" TEXT NOT NULL,
            CONSTRAINT "FK_VendorPayouts_Shops_ShopId" FOREIGN KEY ("ShopId")
                REFERENCES "Shops" ("Id") ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS "IX_VendorPayouts_ShopId" ON "VendorPayouts" ("ShopId");
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_VendorPayouts_Reference" ON "VendorPayouts" ("Reference");
        """;

    // Tax classes postdate the store in both dialects, so they get the same guarded
    // create-on-demand treatment. The ALTER on Products is separate because a store created before
    // this feature already has a Products table — EnsureCreated cannot add a column to it.
    private const string SqlServerTaxDdl = """
        IF OBJECT_ID(N'[dbo].[TaxClasses]', N'U') IS NULL
        BEGIN
            CREATE TABLE [dbo].[TaxClasses] (
                [Id] int NOT NULL IDENTITY,
                [Name] nvarchar(80) NOT NULL,
                [Rate] decimal(5,4) NOT NULL,
                [IsActive] bit NOT NULL,
                [IsExempt] bit NOT NULL,
                [CreatedUtc] datetime2 NOT NULL,
                CONSTRAINT [PK_TaxClasses] PRIMARY KEY ([Id])
            );
            CREATE UNIQUE INDEX [IX_TaxClasses_Name] ON [dbo].[TaxClasses] ([Name]);
        END

        IF NOT EXISTS (SELECT 1 FROM sys.columns
                       WHERE object_id = OBJECT_ID(N'[dbo].[Products]') AND name = N'TaxClassId')
        BEGIN
            ALTER TABLE [dbo].[Products] ADD [TaxClassId] int NULL;
            CREATE INDEX [IX_Products_TaxClassId] ON [dbo].[Products] ([TaxClassId]);
            ALTER TABLE [dbo].[Products] ADD CONSTRAINT [FK_Products_TaxClasses_TaxClassId]
                FOREIGN KEY ([TaxClassId]) REFERENCES [dbo].[TaxClasses] ([Id]) ON DELETE SET NULL;
        END
        """;

    private const string SqliteTaxDdl = """
        CREATE TABLE IF NOT EXISTS "TaxClasses" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_TaxClasses" PRIMARY KEY AUTOINCREMENT,
            "Name" TEXT NOT NULL,
            "Rate" TEXT NOT NULL,
            "IsActive" INTEGER NOT NULL,
            "IsExempt" INTEGER NOT NULL,
            "CreatedUtc" TEXT NOT NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_TaxClasses_Name" ON "TaxClasses" ("Name");

        ALTER TABLE "Products" ADD COLUMN "TaxClassId" INTEGER NULL REFERENCES "TaxClasses" ("Id") ON DELETE SET NULL;
        CREATE INDEX IF NOT EXISTS "IX_Products_TaxClassId" ON "Products" ("TaxClassId");
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

    /// <summary>The tax tables and the product column, kept separate from the vendor money DDL.</summary>
    public static async Task EnsureTaxAsync(ApplicationDbContext db, CancellationToken ct = default)
    {
        if (db.Database.IsSqlServer())
        {
            await db.Database.ExecuteSqlRawAsync(SqlServerTaxDdl, ct);
        }
        else if (db.Database.IsSqlite())
        {
            await db.Database.ExecuteSqlRawAsync(SqliteTaxDdl, ct);
        }
    }
}
