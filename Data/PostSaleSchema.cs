using Microsoft.EntityFrameworkCore;

namespace WebDS_Test.Data;

/// <summary>
/// Guarantees the post-sale tables exist in a store created before these processes were modelled.
/// </summary>
/// <remarks>
/// <c>EnsureCreated</c> builds the whole schema only when the database is empty, so it cannot add
/// tables to the deployed <c>MVStores</c> store or an existing SQLite development file. The same
/// problem <see cref="ListingSchema"/> and <see cref="ReturnsSchema"/> solve for columns; this guard
/// solves it for whole tables. Every statement is written to be idempotent, so starting the app
/// twice is harmless.
/// </remarks>
public static class PostSaleSchema
{
    private const string SqlServerDdl = """
        IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'PostSaleRequests' AND schema_id = SCHEMA_ID('dbo'))
        BEGIN
            CREATE TABLE [dbo].[PostSaleRequests] (
                [Id] int NOT NULL IDENTITY,
                [Reference] nvarchar(32) NOT NULL,
                [OrderId] int NOT NULL,
                [OrderLineId] int NULL,
                [ShopId] int NOT NULL,
                [CustomerId] nvarchar(450) NOT NULL,
                [Type] int NOT NULL,
                [Reason] int NOT NULL,
                [ReasonDetail] nvarchar(1000) NULL,
                [Status] int NOT NULL,
                [IsOrderLevel] bit NOT NULL,
                [ItemPriceBearer] int NOT NULL,
                [ShippingFeeBearer] int NOT NULL,
                [CreditCardFeeBearer] int NOT NULL,
                [PickupFeeBearer] int NOT NULL,
                [ReturnFeeBearer] int NOT NULL,
                [ReplacementFeeBearer] int NOT NULL,
                [RefundAmount] decimal(18,2) NOT NULL,
                [IsPromoCodeRefund] bit NOT NULL,
                [PromoCode] nvarchar(64) NULL,
                [ReplacementVariantId] int NULL,
                [SerialNumber] nvarchar(100) NULL,
                [PurchaseDateUtc] datetime2 NULL,
                [GracePeriodEndUtc] datetime2 NULL,
                [WithinGracePeriod] bit NOT NULL,
                [CanBeRepaired] bit NOT NULL,
                [CanBeReplaced] bit NOT NULL,
                [Carrier] nvarchar(40) NULL,
                [CallAttempts] int NOT NULL,
                [ManifestNumber] nvarchar(50) NULL,
                [QualityReport] nvarchar(2000) NULL,
                [RedeliveryAttempted] bit NOT NULL,
                [PickupTrackingNumber] nvarchar(100) NULL,
                [PickupScheduledUtc] datetime2 NULL,
                [PickedUpUtc] datetime2 NULL,
                [ReceivedAtWarehouseUtc] datetime2 NULL,
                [AdminNotes] nvarchar(2000) NULL,
                [CreatedUtc] datetime2 NOT NULL,
                [UpdatedUtc] datetime2 NOT NULL,
                [ProcessedUtc] datetime2 NULL,
                [ClosedUtc] datetime2 NULL,
                [ProcessedBy] nvarchar(256) NULL,
                [ClosedBy] nvarchar(256) NULL,
                CONSTRAINT [PK_PostSaleRequests] PRIMARY KEY ([Id]),
                CONSTRAINT [FK_PostSaleRequests_Orders_OrderId] FOREIGN KEY ([OrderId]) REFERENCES [dbo].[Orders] ([Id]) ON DELETE NO ACTION,
                CONSTRAINT [FK_PostSaleRequests_OrderLines_OrderLineId] FOREIGN KEY ([OrderLineId]) REFERENCES [dbo].[OrderLines] ([Id]) ON DELETE NO ACTION,
                CONSTRAINT [FK_PostSaleRequests_Shops_ShopId] FOREIGN KEY ([ShopId]) REFERENCES [dbo].[Shops] ([Id]) ON DELETE NO ACTION,
                CONSTRAINT [FK_PostSaleRequests_Users_CustomerId] FOREIGN KEY ([CustomerId]) REFERENCES [dbo].[Users] ([Id]) ON DELETE NO ACTION,
                CONSTRAINT [FK_PostSaleRequests_ProductVariants_ReplacementVariantId] FOREIGN KEY ([ReplacementVariantId]) REFERENCES [dbo].[ProductVariants] ([Id]) ON DELETE SET NULL
            );
            CREATE UNIQUE INDEX [IX_PostSaleRequests_Reference] ON [dbo].[PostSaleRequests] ([Reference]);
            CREATE INDEX [IX_PostSaleRequests_OrderId] ON [dbo].[PostSaleRequests] ([OrderId]);
            CREATE INDEX [IX_PostSaleRequests_ShopId] ON [dbo].[PostSaleRequests] ([ShopId]);
            CREATE INDEX [IX_PostSaleRequests_CustomerId] ON [dbo].[PostSaleRequests] ([CustomerId]);
            CREATE INDEX [IX_PostSaleRequests_Status] ON [dbo].[PostSaleRequests] ([Status]);
            CREATE INDEX [IX_PostSaleRequests_Type] ON [dbo].[PostSaleRequests] ([Type]);
        END

        IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'PostSaleMessages' AND schema_id = SCHEMA_ID('dbo'))
        BEGIN
            CREATE TABLE [dbo].[PostSaleMessages] (
                [Id] int NOT NULL IDENTITY,
                [PostSaleRequestId] int NOT NULL,
                [SenderId] nvarchar(64) NOT NULL,
                [SenderName] nvarchar(256) NOT NULL,
                [IsFromCustomer] bit NOT NULL,
                [Content] nvarchar(2000) NOT NULL,
                [CreatedUtc] datetime2 NOT NULL,
                CONSTRAINT [PK_PostSaleMessages] PRIMARY KEY ([Id]),
                CONSTRAINT [FK_PostSaleMessages_PostSaleRequests_PostSaleRequestId] FOREIGN KEY ([PostSaleRequestId]) REFERENCES [dbo].[PostSaleRequests] ([Id]) ON DELETE CASCADE
            );
            CREATE INDEX [IX_PostSaleMessages_PostSaleRequestId] ON [dbo].[PostSaleMessages] ([PostSaleRequestId]);
        END

        IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'PostSaleStatusHistory' AND schema_id = SCHEMA_ID('dbo'))
        BEGIN
            CREATE TABLE [dbo].[PostSaleStatusHistory] (
                [Id] int NOT NULL IDENTITY,
                [PostSaleRequestId] int NOT NULL,
                [FromStatus] int NOT NULL,
                [ToStatus] int NOT NULL,
                [ChangedBy] nvarchar(256) NULL,
                [Note] nvarchar(1000) NULL,
                [CreatedUtc] datetime2 NOT NULL,
                CONSTRAINT [PK_PostSaleStatusHistory] PRIMARY KEY ([Id]),
                CONSTRAINT [FK_PostSaleStatusHistory_PostSaleRequests_PostSaleRequestId] FOREIGN KEY ([PostSaleRequestId]) REFERENCES [dbo].[PostSaleRequests] ([Id]) ON DELETE CASCADE
            );
            CREATE INDEX [IX_PostSaleStatusHistory_PostSaleRequestId] ON [dbo].[PostSaleStatusHistory] ([PostSaleRequestId]);
        END

        IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'SellerFulfilments' AND schema_id = SCHEMA_ID('dbo'))
        BEGIN
            CREATE TABLE [dbo].[SellerFulfilments] (
                [Id] int NOT NULL IDENTITY,
                [OrderId] int NOT NULL,
                [ShopId] int NOT NULL,
                [PackageCount] int NOT NULL,
                [HasNylonPockets] bit NOT NULL,
                [InvoiceAttached] bit NOT NULL,
                [LabelsGenerated] int NOT NULL,
                [SellerConfirmedUtc] datetime2 NULL,
                [LabelsGeneratedUtc] datetime2 NULL,
                [PickedUpByShipperUtc] datetime2 NULL,
                [Status] int NOT NULL,
                [CreatedUtc] datetime2 NOT NULL,
                [UpdatedUtc] datetime2 NOT NULL,
                CONSTRAINT [PK_SellerFulfilments] PRIMARY KEY ([Id]),
                CONSTRAINT [FK_SellerFulfilments_Orders_OrderId] FOREIGN KEY ([OrderId]) REFERENCES [dbo].[Orders] ([Id]) ON DELETE NO ACTION,
                CONSTRAINT [FK_SellerFulfilments_Shops_ShopId] FOREIGN KEY ([ShopId]) REFERENCES [dbo].[Shops] ([Id]) ON DELETE NO ACTION
            );
            CREATE UNIQUE INDEX [IX_SellerFulfilments_OrderId] ON [dbo].[SellerFulfilments] ([OrderId]);
            CREATE INDEX [IX_SellerFulfilments_ShopId] ON [dbo].[SellerFulfilments] ([ShopId]);
            CREATE INDEX [IX_SellerFulfilments_Status] ON [dbo].[SellerFulfilments] ([Status]);
        END
        """;

    // SQLite: CREATE TABLE IF NOT EXISTS makes the guard naturally idempotent. Types follow what
    // EF Core's SQLite provider would have generated for the same model (TEXT for money and dates).
    private const string SqliteDdl = """
        CREATE TABLE IF NOT EXISTS "PostSaleRequests" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_PostSaleRequests" PRIMARY KEY AUTOINCREMENT,
            "Reference" TEXT NOT NULL,
            "OrderId" INTEGER NOT NULL,
            "OrderLineId" INTEGER NULL,
            "ShopId" INTEGER NOT NULL,
            "CustomerId" TEXT NOT NULL,
            "Type" INTEGER NOT NULL,
            "Reason" INTEGER NOT NULL,
            "ReasonDetail" TEXT NULL,
            "Status" INTEGER NOT NULL,
            "IsOrderLevel" INTEGER NOT NULL,
            "ItemPriceBearer" INTEGER NOT NULL,
            "ShippingFeeBearer" INTEGER NOT NULL,
            "CreditCardFeeBearer" INTEGER NOT NULL,
            "PickupFeeBearer" INTEGER NOT NULL,
            "ReturnFeeBearer" INTEGER NOT NULL,
            "ReplacementFeeBearer" INTEGER NOT NULL,
            "RefundAmount" TEXT NOT NULL,
            "IsPromoCodeRefund" INTEGER NOT NULL,
            "PromoCode" TEXT NULL,
            "ReplacementVariantId" INTEGER NULL,
            "SerialNumber" TEXT NULL,
            "PurchaseDateUtc" TEXT NULL,
            "GracePeriodEndUtc" TEXT NULL,
            "WithinGracePeriod" INTEGER NOT NULL,
            "CanBeRepaired" INTEGER NOT NULL,
            "CanBeReplaced" INTEGER NOT NULL,
            "Carrier" TEXT NULL,
            "CallAttempts" INTEGER NOT NULL,
            "ManifestNumber" TEXT NULL,
            "QualityReport" TEXT NULL,
            "RedeliveryAttempted" INTEGER NOT NULL,
            "PickupTrackingNumber" TEXT NULL,
            "PickupScheduledUtc" TEXT NULL,
            "PickedUpUtc" TEXT NULL,
            "ReceivedAtWarehouseUtc" TEXT NULL,
            "AdminNotes" TEXT NULL,
            "CreatedUtc" TEXT NOT NULL,
            "UpdatedUtc" TEXT NOT NULL,
            "ProcessedUtc" TEXT NULL,
            "ClosedUtc" TEXT NULL,
            "ProcessedBy" TEXT NULL,
            "ClosedBy" TEXT NULL,
            CONSTRAINT "FK_PostSaleRequests_Orders_OrderId" FOREIGN KEY ("OrderId") REFERENCES "Orders" ("Id"),
            CONSTRAINT "FK_PostSaleRequests_OrderLines_OrderLineId" FOREIGN KEY ("OrderLineId") REFERENCES "OrderLines" ("Id"),
            CONSTRAINT "FK_PostSaleRequests_Shops_ShopId" FOREIGN KEY ("ShopId") REFERENCES "Shops" ("Id"),
            CONSTRAINT "FK_PostSaleRequests_Users_CustomerId" FOREIGN KEY ("CustomerId") REFERENCES "Users" ("Id"),
            CONSTRAINT "FK_PostSaleRequests_ProductVariants_ReplacementVariantId" FOREIGN KEY ("ReplacementVariantId") REFERENCES "ProductVariants" ("Id") ON DELETE SET NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_PostSaleRequests_Reference" ON "PostSaleRequests" ("Reference");
        CREATE INDEX IF NOT EXISTS "IX_PostSaleRequests_OrderId" ON "PostSaleRequests" ("OrderId");
        CREATE INDEX IF NOT EXISTS "IX_PostSaleRequests_ShopId" ON "PostSaleRequests" ("ShopId");
        CREATE INDEX IF NOT EXISTS "IX_PostSaleRequests_CustomerId" ON "PostSaleRequests" ("CustomerId");
        CREATE INDEX IF NOT EXISTS "IX_PostSaleRequests_Status" ON "PostSaleRequests" ("Status");
        CREATE INDEX IF NOT EXISTS "IX_PostSaleRequests_Type" ON "PostSaleRequests" ("Type");

        CREATE TABLE IF NOT EXISTS "PostSaleMessages" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_PostSaleMessages" PRIMARY KEY AUTOINCREMENT,
            "PostSaleRequestId" INTEGER NOT NULL,
            "SenderId" TEXT NOT NULL,
            "SenderName" TEXT NOT NULL,
            "IsFromCustomer" INTEGER NOT NULL,
            "Content" TEXT NOT NULL,
            "CreatedUtc" TEXT NOT NULL,
            CONSTRAINT "FK_PostSaleMessages_PostSaleRequests_PostSaleRequestId" FOREIGN KEY ("PostSaleRequestId") REFERENCES "PostSaleRequests" ("Id") ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS "IX_PostSaleMessages_PostSaleRequestId" ON "PostSaleMessages" ("PostSaleRequestId");

        CREATE TABLE IF NOT EXISTS "PostSaleStatusHistory" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_PostSaleStatusHistory" PRIMARY KEY AUTOINCREMENT,
            "PostSaleRequestId" INTEGER NOT NULL,
            "FromStatus" INTEGER NOT NULL,
            "ToStatus" INTEGER NOT NULL,
            "ChangedBy" TEXT NULL,
            "Note" TEXT NULL,
            "CreatedUtc" TEXT NOT NULL,
            CONSTRAINT "FK_PostSaleStatusHistory_PostSaleRequests_PostSaleRequestId" FOREIGN KEY ("PostSaleRequestId") REFERENCES "PostSaleRequests" ("Id") ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS "IX_PostSaleStatusHistory_PostSaleRequestId" ON "PostSaleStatusHistory" ("PostSaleRequestId");

        CREATE TABLE IF NOT EXISTS "SellerFulfilments" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_SellerFulfilments" PRIMARY KEY AUTOINCREMENT,
            "OrderId" INTEGER NOT NULL,
            "ShopId" INTEGER NOT NULL,
            "PackageCount" INTEGER NOT NULL,
            "HasNylonPockets" INTEGER NOT NULL,
            "InvoiceAttached" INTEGER NOT NULL,
            "LabelsGenerated" INTEGER NOT NULL,
            "SellerConfirmedUtc" TEXT NULL,
            "LabelsGeneratedUtc" TEXT NULL,
            "PickedUpByShipperUtc" TEXT NULL,
            "Status" INTEGER NOT NULL,
            "CreatedUtc" TEXT NOT NULL,
            "UpdatedUtc" TEXT NOT NULL,
            CONSTRAINT "FK_SellerFulfilments_Orders_OrderId" FOREIGN KEY ("OrderId") REFERENCES "Orders" ("Id"),
            CONSTRAINT "FK_SellerFulfilments_Shops_ShopId" FOREIGN KEY ("ShopId") REFERENCES "Shops" ("Id")
        );
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_SellerFulfilments_OrderId" ON "SellerFulfilments" ("OrderId");
        CREATE INDEX IF NOT EXISTS "IX_SellerFulfilments_ShopId" ON "SellerFulfilments" ("ShopId");
        CREATE INDEX IF NOT EXISTS "IX_SellerFulfilments_Status" ON "SellerFulfilments" ("Status");
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
