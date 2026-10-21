using Microsoft.EntityFrameworkCore;

namespace WebDS_Test.Data;

/// <summary>
/// Guarantees the conversation tables exist on a store created before messaging was modelled.
/// </summary>
/// <remarks>
/// <para>
/// The same problem <see cref="ReturnsSchema"/>, <see cref="AuditSchema"/> and
/// <see cref="VendorSchema"/> solve: <c>EnsureCreated</c> builds the whole schema only when the
/// database is empty, so a store that predates <c>Conversations</c>/<c>ConversationMessages</c>
/// needs the two tables added on demand.
/// </para>
/// <para>
/// The create statements match what EF Core generates for <see cref="Domain.Conversation"/> and
/// <see cref="Domain.ConversationMessage"/> — column names, lengths, nullability, indexes and
/// delete rules match the model — so a database patched here is indistinguishable at run time from
/// one built by <c>EnsureCreated</c>. SQLite has no <c>IF NOT EXISTS</c> form of anything except
/// <c>CREATE TABLE</c>, so its statements carry the same guards the other patches use.
/// </para>
/// <para>
/// The statements are static and carry no parameters and no user input.
/// </para>
/// </remarks>
public static class ConversationSchema
{
    private const string SqlServerDdl = """
        IF OBJECT_ID(N'[dbo].[Conversations]', N'U') IS NULL
        BEGIN
            CREATE TABLE [dbo].[Conversations] (
                [Id] int NOT NULL IDENTITY,
                [Reference] nvarchar(32) NOT NULL,
                [ShopId] int NOT NULL,
                [OrderId] int NULL,
                [UserId] nvarchar(450) NULL,
                [CustomerEmail] nvarchar(256) NOT NULL,
                [Subject] nvarchar(200) NOT NULL,
                [Status] int NOT NULL,
                [LastMessagePreview] nvarchar(300) NOT NULL,
                [LastAuthor] int NOT NULL,
                [LastMessageUtc] datetime2 NOT NULL,
                [CreatedUtc] datetime2 NOT NULL,
                [ClosedUtc] datetime2 NULL,
                [CustomerLastReadUtc] datetime2 NULL,
                [VendorLastReadUtc] datetime2 NULL,
                CONSTRAINT [PK_Conversations] PRIMARY KEY ([Id]),
                CONSTRAINT [FK_Conversations_Shops_ShopId] FOREIGN KEY ([ShopId])
                    REFERENCES [dbo].[Shops] ([Id]),
                CONSTRAINT [FK_Conversations_Orders_OrderId] FOREIGN KEY ([OrderId])
                    REFERENCES [dbo].[Orders] ([Id]) ON DELETE SET NULL,
                CONSTRAINT [FK_Conversations_Users_UserId] FOREIGN KEY ([UserId])
                    REFERENCES [dbo].[Users] ([Id]) ON DELETE SET NULL
            );
            CREATE UNIQUE INDEX [IX_Conversations_Reference] ON [dbo].[Conversations] ([Reference]);
            CREATE INDEX [IX_Conversations_ShopId] ON [dbo].[Conversations] ([ShopId]);
            CREATE INDEX [IX_Conversations_UserId] ON [dbo].[Conversations] ([UserId]);
            CREATE INDEX [IX_Conversations_Status] ON [dbo].[Conversations] ([Status]);
            CREATE INDEX [IX_Conversations_LastMessageUtc] ON [dbo].[Conversations] ([LastMessageUtc]);
            CREATE INDEX [IX_Conversations_OrderId] ON [dbo].[Conversations] ([OrderId]);
        END

        IF OBJECT_ID(N'[dbo].[ConversationMessages]', N'U') IS NULL
        BEGIN
            CREATE TABLE [dbo].[ConversationMessages] (
                [Id] int NOT NULL IDENTITY,
                [ConversationId] int NOT NULL,
                [Author] int NOT NULL,
                [SenderName] nvarchar(120) NOT NULL,
                [SenderUserId] nvarchar(450) NULL,
                [Body] nvarchar(4000) NOT NULL,
                [SentUtc] datetime2 NOT NULL,
                CONSTRAINT [PK_ConversationMessages] PRIMARY KEY ([Id]),
                CONSTRAINT [FK_ConversationMessages_Conversations_ConversationId] FOREIGN KEY ([ConversationId])
                    REFERENCES [dbo].[Conversations] ([Id]) ON DELETE CASCADE
            );
            CREATE INDEX [IX_ConversationMessages_ConversationId] ON [dbo].[ConversationMessages] ([ConversationId]);
            CREATE INDEX [IX_ConversationMessages_SentUtc] ON [dbo].[ConversationMessages] ([SentUtc]);
        END
        """;

    private const string SqliteDdl = """
        CREATE TABLE IF NOT EXISTS "Conversations" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_Conversations" PRIMARY KEY AUTOINCREMENT,
            "Reference" TEXT NOT NULL,
            "ShopId" INTEGER NOT NULL,
            "OrderId" INTEGER NULL,
            "UserId" TEXT NULL,
            "CustomerEmail" TEXT NOT NULL,
            "Subject" TEXT NOT NULL,
            "Status" INTEGER NOT NULL,
            "LastMessagePreview" TEXT NOT NULL,
            "LastAuthor" INTEGER NOT NULL,
            "LastMessageUtc" TEXT NOT NULL,
            "CreatedUtc" TEXT NOT NULL,
            "ClosedUtc" TEXT NULL,
            "CustomerLastReadUtc" TEXT NULL,
            "VendorLastReadUtc" TEXT NULL,
            CONSTRAINT "FK_Conversations_Shops_ShopId" FOREIGN KEY ("ShopId")
                REFERENCES "Shops" ("Id") ON DELETE RESTRICT,
            CONSTRAINT "FK_Conversations_Orders_OrderId" FOREIGN KEY ("OrderId")
                REFERENCES "Orders" ("Id") ON DELETE SET NULL,
            CONSTRAINT "FK_Conversations_Users_UserId" FOREIGN KEY ("UserId")
                REFERENCES "Users" ("Id") ON DELETE SET NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_Conversations_Reference" ON "Conversations" ("Reference");
        CREATE INDEX IF NOT EXISTS "IX_Conversations_ShopId" ON "Conversations" ("ShopId");
        CREATE INDEX IF NOT EXISTS "IX_Conversations_UserId" ON "Conversations" ("UserId");
        CREATE INDEX IF NOT EXISTS "IX_Conversations_Status" ON "Conversations" ("Status");
        CREATE INDEX IF NOT EXISTS "IX_Conversations_LastMessageUtc" ON "Conversations" ("LastMessageUtc");
        CREATE INDEX IF NOT EXISTS "IX_Conversations_OrderId" ON "Conversations" ("OrderId");

        CREATE TABLE IF NOT EXISTS "ConversationMessages" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_ConversationMessages" PRIMARY KEY AUTOINCREMENT,
            "ConversationId" INTEGER NOT NULL,
            "Author" INTEGER NOT NULL,
            "SenderName" TEXT NOT NULL,
            "SenderUserId" TEXT NULL,
            "Body" TEXT NOT NULL,
            "SentUtc" TEXT NOT NULL,
            CONSTRAINT "FK_ConversationMessages_Conversations_ConversationId" FOREIGN KEY ("ConversationId")
                REFERENCES "Conversations" ("Id") ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS "IX_ConversationMessages_ConversationId" ON "ConversationMessages" ("ConversationId");
        CREATE INDEX IF NOT EXISTS "IX_ConversationMessages_SentUtc" ON "ConversationMessages" ("SentUtc");
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
