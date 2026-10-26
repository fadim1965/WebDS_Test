using Microsoft.EntityFrameworkCore;

namespace WebDS_Test.Data;

/// <summary>
/// Guarantees the carrier-remittance tables exist in a store created before COD reconciliation
/// was modelled. Same idempotent guard pattern as <see cref="PostSaleSchema"/>.
/// </summary>
public static class CodRemittanceSchema
{
    private const string SqlServerDdl = """
        IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'CarrierRemittances' AND schema_id = SCHEMA_ID('dbo'))
        BEGIN
            CREATE TABLE [dbo].[CarrierRemittances] (
                [Id] int NOT NULL IDENTITY,
                [Carrier] nvarchar(40) NOT NULL,
                [Reference] nvarchar(80) NOT NULL,
                [BankReference] nvarchar(120) NULL,
                [RemittanceDateUtc] datetime2 NOT NULL,
                [ReceivedUtc] datetime2 NOT NULL,
                [ReceivedBy] nvarchar(256) NULL,
                [Notes] nvarchar(500) NULL,
                [Status] int NOT NULL,
                [ExpectedAmount] decimal(18,2) NOT NULL,
                [ActualAmount] decimal(18,2) NOT NULL,
                [Variance] decimal(18,2) NOT NULL,
                [Tolerance] decimal(18,2) NOT NULL,
                [ReconciledUtc] datetime2 NULL,
                [ReconciledBy] nvarchar(256) NULL,
                [ClosedUtc] datetime2 NULL,
                [ClosedBy] nvarchar(256) NULL,
                CONSTRAINT [PK_CarrierRemittances] PRIMARY KEY ([Id])
            );
            CREATE UNIQUE INDEX [IX_CarrierRemittances_Carrier_Reference] ON [dbo].[CarrierRemittances] ([Carrier], [Reference]);
            CREATE INDEX [IX_CarrierRemittances_Status] ON [dbo].[CarrierRemittances] ([Status]);
        END

        IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'CarrierRemittanceLines' AND schema_id = SCHEMA_ID('dbo'))
        BEGIN
            CREATE TABLE [dbo].[CarrierRemittanceLines] (
                [Id] int NOT NULL IDENTITY,
                [CarrierRemittanceId] int NOT NULL,
                [OrderId] int NULL,
                [TrackingNumber] nvarchar(120) NULL,
                [ExpectedAmount] decimal(18,2) NOT NULL,
                [ActualAmount] decimal(18,2) NOT NULL,
                [Variance] decimal(18,2) NOT NULL,
                [Status] int NOT NULL,
                [Note] nvarchar(300) NULL,
                [SettledUtc] datetime2 NULL,
                CONSTRAINT [PK_CarrierRemittanceLines] PRIMARY KEY ([Id]),
                CONSTRAINT [FK_CarrierRemittanceLines_CarrierRemittances_CarrierRemittanceId] FOREIGN KEY ([CarrierRemittanceId]) REFERENCES [dbo].[CarrierRemittances] ([Id]) ON DELETE CASCADE,
                CONSTRAINT [FK_CarrierRemittanceLines_Orders_OrderId] FOREIGN KEY ([OrderId]) REFERENCES [dbo].[Orders] ([Id]) ON DELETE NO ACTION
            );
            CREATE INDEX [IX_CarrierRemittanceLines_CarrierRemittanceId] ON [dbo].[CarrierRemittanceLines] ([CarrierRemittanceId]);
            CREATE INDEX [IX_CarrierRemittanceLines_OrderId] ON [dbo].[CarrierRemittanceLines] ([OrderId]);
        END
        """;

    private const string SqliteDdl = """
        CREATE TABLE IF NOT EXISTS "CarrierRemittances" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_CarrierRemittances" PRIMARY KEY AUTOINCREMENT,
            "Carrier" TEXT NOT NULL,
            "Reference" TEXT NOT NULL,
            "BankReference" TEXT NULL,
            "RemittanceDateUtc" TEXT NOT NULL,
            "ReceivedUtc" TEXT NOT NULL,
            "ReceivedBy" TEXT NULL,
            "Notes" TEXT NULL,
            "Status" INTEGER NOT NULL,
            "ExpectedAmount" TEXT NOT NULL,
            "ActualAmount" TEXT NOT NULL,
            "Variance" TEXT NOT NULL,
            "Tolerance" TEXT NOT NULL,
            "ReconciledUtc" TEXT NULL,
            "ReconciledBy" TEXT NULL,
            "ClosedUtc" TEXT NULL,
            "ClosedBy" TEXT NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_CarrierRemittances_Carrier_Reference" ON "CarrierRemittances" ("Carrier", "Reference");
        CREATE INDEX IF NOT EXISTS "IX_CarrierRemittances_Status" ON "CarrierRemittances" ("Status");

        CREATE TABLE IF NOT EXISTS "CarrierRemittanceLines" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_CarrierRemittanceLines" PRIMARY KEY AUTOINCREMENT,
            "CarrierRemittanceId" INTEGER NOT NULL,
            "OrderId" INTEGER NULL,
            "TrackingNumber" TEXT NULL,
            "ExpectedAmount" TEXT NOT NULL,
            "ActualAmount" TEXT NOT NULL,
            "Variance" TEXT NOT NULL,
            "Status" INTEGER NOT NULL,
            "Note" TEXT NULL,
            "SettledUtc" TEXT NULL,
            CONSTRAINT "FK_CarrierRemittanceLines_CarrierRemittances_CarrierRemittanceId" FOREIGN KEY ("CarrierRemittanceId") REFERENCES "CarrierRemittances" ("Id") ON DELETE CASCADE,
            CONSTRAINT "FK_CarrierRemittanceLines_Orders_OrderId" FOREIGN KEY ("OrderId") REFERENCES "Orders" ("Id")
        );
        CREATE INDEX IF NOT EXISTS "IX_CarrierRemittanceLines_CarrierRemittanceId" ON "CarrierRemittanceLines" ("CarrierRemittanceId");
        CREATE INDEX IF NOT EXISTS "IX_CarrierRemittanceLines_OrderId" ON "CarrierRemittanceLines" ("OrderId");
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
