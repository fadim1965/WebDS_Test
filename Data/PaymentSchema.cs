using Microsoft.EntityFrameworkCore;

namespace WebDS_Test.Data;

/// <summary>
/// Guarantees that the received-payment-webhook table exists in a store created before it was
/// modelled.
/// </summary>
/// <remarks>
/// <para>
/// The same problem <see cref="CouponSchema"/>, <see cref="ReviewSchema"/>, <see cref="EmailSchema"/>
/// and <see cref="LogTable"/> solve: <c>EnsureCreated</c> builds the whole schema only when the
/// database is empty, so it cannot add <c>PaymentEvents</c> to the deployed <c>MVStores</c> store or
/// the SQLite development file. The table is created on demand with guarded DDL instead.
/// </para>
/// <para>
/// The statements are what EF Core generates for <see cref="Domain.PaymentEvent"/> — column names,
/// lengths and nullability match the model — so a database patched here is indistinguishable at run
/// time from one built by <c>EnsureCreated</c>. The statements carry no parameters and no user input.
/// </para>
/// </remarks>
public static class PaymentSchema
{
    private const string SqlServerDdl = """
        IF OBJECT_ID(N'[dbo].[PaymentEvents]', N'U') IS NULL
        BEGIN
            CREATE TABLE [dbo].[PaymentEvents] (
                [Id] int NOT NULL IDENTITY,
                [Provider] nvarchar(40) NOT NULL,
                [EventId] nvarchar(120) NOT NULL,
                [Type] nvarchar(60) NOT NULL,
                [OrderId] int NULL,
                [OrderNumber] nvarchar(32) NULL,
                [TransactionId] nvarchar(120) NULL,
                [Amount] decimal(18,2) NOT NULL,
                [Currency] nvarchar(8) NOT NULL,
                [Payload] nvarchar(max) NOT NULL,
                [SignatureValid] bit NOT NULL,
                [Outcome] nvarchar(40) NOT NULL,
                [Note] nvarchar(500) NULL,
                [CreatedUtc] datetime2 NOT NULL,
                CONSTRAINT [PK_PaymentEvents] PRIMARY KEY ([Id]),
                CONSTRAINT [FK_PaymentEvents_Orders_OrderId] FOREIGN KEY ([OrderId])
                    REFERENCES [dbo].[Orders] ([Id]) ON DELETE SET NULL
            );
            CREATE UNIQUE INDEX [IX_PaymentEvents_Provider_EventId] ON [dbo].[PaymentEvents] ([Provider], [EventId]);
            CREATE INDEX [IX_PaymentEvents_OrderId] ON [dbo].[PaymentEvents] ([OrderId]);
            CREATE INDEX [IX_PaymentEvents_CreatedUtc] ON [dbo].[PaymentEvents] ([CreatedUtc]);
        END
        """;

    private const string SqliteDdl = """
        CREATE TABLE IF NOT EXISTS "PaymentEvents" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_PaymentEvents" PRIMARY KEY AUTOINCREMENT,
            "Provider" TEXT NOT NULL,
            "EventId" TEXT NOT NULL,
            "Type" TEXT NOT NULL,
            "OrderId" INTEGER NULL,
            "OrderNumber" TEXT NULL,
            "TransactionId" TEXT NULL,
            "Amount" TEXT NOT NULL,
            "Currency" TEXT NOT NULL,
            "Payload" TEXT NOT NULL,
            "SignatureValid" INTEGER NOT NULL,
            "Outcome" TEXT NOT NULL,
            "Note" TEXT NULL,
            "CreatedUtc" TEXT NOT NULL,
            CONSTRAINT "FK_PaymentEvents_Orders_OrderId" FOREIGN KEY ("OrderId")
                REFERENCES "Orders" ("Id") ON DELETE SET NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_PaymentEvents_Provider_EventId" ON "PaymentEvents" ("Provider", "EventId");
        CREATE INDEX IF NOT EXISTS "IX_PaymentEvents_OrderId" ON "PaymentEvents" ("OrderId");
        CREATE INDEX IF NOT EXISTS "IX_PaymentEvents_CreatedUtc" ON "PaymentEvents" ("CreatedUtc");
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
