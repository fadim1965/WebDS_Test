namespace WebDS_Test.Data;

/// <summary>
/// Guarantees that the review tables exist in a store that was created before they were modelled.
/// </summary>
/// <remarks>
/// <para>
/// <c>EnsureCreated</c> builds the whole schema only when the database is empty, so it cannot add
/// <c>Reviews</c>/<c>ReviewVotes</c> to the deployed <c>MVStores</c> store (or the SQLite
/// development file). The same problem the log sink had, solved the same way: create the tables on
/// demand with guarded DDL.
/// </para>
/// <para>
/// The statements are exactly what EF Core generates for <see cref="Domain.Review"/> and
/// <see cref="Domain.ReviewVote"/> — column names, lengths and nullability match the model, so a
/// database built by this patch is indistinguishable at runtime from one built by
/// <c>EnsureCreated</c>. Both dialects are guarded (<c>IF … IS NULL</c>, <c>IF NOT EXISTS</c>), so
/// running them on every start is a no-op once the tables are present. They carry no parameters and
/// no user input.
/// </para>
/// </remarks>
public static class ReviewSchema
{
    private const string SqlServerDdl = """
        IF OBJECT_ID(N'[dbo].[Reviews]', N'U') IS NULL
        BEGIN
            CREATE TABLE [dbo].[Reviews] (
                [Id] int NOT NULL IDENTITY,
                [ProductId] int NOT NULL,
                [ShopId] int NOT NULL,
                [UserId] nvarchar(450) NULL,
                [ReviewerName] nvarchar(120) NOT NULL,
                [Rating] int NOT NULL,
                [Title] nvarchar(140) NULL,
                [Body] nvarchar(4000) NULL,
                [IsVerifiedPurchase] bit NOT NULL,
                [Status] int NOT NULL,
                [HelpfulCount] int NOT NULL,
                [NotHelpfulCount] int NOT NULL,
                [VendorReply] nvarchar(2000) NULL,
                [VendorReplyUtc] datetime2 NULL,
                [CreatedUtc] datetime2 NOT NULL,
                [PublishedUtc] datetime2 NULL,
                [UpdatedUtc] datetime2 NULL,
                CONSTRAINT [PK_Reviews] PRIMARY KEY ([Id]),
                CONSTRAINT [FK_Reviews_Products_ProductId] FOREIGN KEY ([ProductId])
                    REFERENCES [dbo].[Products] ([Id]),
                CONSTRAINT [FK_Reviews_Shops_ShopId] FOREIGN KEY ([ShopId])
                    REFERENCES [dbo].[Shops] ([Id]),
                CONSTRAINT [FK_Reviews_Users_UserId] FOREIGN KEY ([UserId])
                    REFERENCES [dbo].[Users] ([Id]) ON DELETE SET NULL
            );
            CREATE INDEX [IX_Reviews_ProductId] ON [dbo].[Reviews] ([ProductId]);
            CREATE INDEX [IX_Reviews_ShopId] ON [dbo].[Reviews] ([ShopId]);
            CREATE INDEX [IX_Reviews_UserId] ON [dbo].[Reviews] ([UserId]);
            CREATE INDEX [IX_Reviews_ProductId_Status] ON [dbo].[Reviews] ([ProductId], [Status]);
            CREATE INDEX [IX_Reviews_Status] ON [dbo].[Reviews] ([Status]);
            CREATE INDEX [IX_Reviews_CreatedUtc] ON [dbo].[Reviews] ([CreatedUtc]);
        END

        IF OBJECT_ID(N'[dbo].[ReviewVotes]', N'U') IS NULL
        BEGIN
            CREATE TABLE [dbo].[ReviewVotes] (
                [Id] int NOT NULL IDENTITY,
                [ReviewId] int NOT NULL,
                [UserId] nvarchar(450) NOT NULL,
                [IsHelpful] bit NOT NULL,
                [CreatedUtc] datetime2 NOT NULL,
                CONSTRAINT [PK_ReviewVotes] PRIMARY KEY ([Id]),
                CONSTRAINT [FK_ReviewVotes_Reviews_ReviewId] FOREIGN KEY ([ReviewId])
                    REFERENCES [dbo].[Reviews] ([Id]) ON DELETE CASCADE,
                CONSTRAINT [FK_ReviewVotes_Users_UserId] FOREIGN KEY ([UserId])
                    REFERENCES [dbo].[Users] ([Id]) ON DELETE CASCADE
            );
            CREATE INDEX [IX_ReviewVotes_ReviewId] ON [dbo].[ReviewVotes] ([ReviewId]);
            CREATE UNIQUE INDEX [IX_ReviewVotes_ReviewId_UserId] ON [dbo].[ReviewVotes] ([ReviewId], [UserId]);
        END
        """;

    private const string SqliteDdl = """
        CREATE TABLE IF NOT EXISTS "Reviews" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_Reviews" PRIMARY KEY AUTOINCREMENT,
            "ProductId" INTEGER NOT NULL,
            "ShopId" INTEGER NOT NULL,
            "UserId" TEXT NULL,
            "ReviewerName" TEXT NOT NULL,
            "Rating" INTEGER NOT NULL,
            "Title" TEXT NULL,
            "Body" TEXT NULL,
            "IsVerifiedPurchase" INTEGER NOT NULL,
            "Status" INTEGER NOT NULL,
            "HelpfulCount" INTEGER NOT NULL,
            "NotHelpfulCount" INTEGER NOT NULL,
            "VendorReply" TEXT NULL,
            "VendorReplyUtc" TEXT NULL,
            "CreatedUtc" TEXT NOT NULL,
            "PublishedUtc" TEXT NULL,
            "UpdatedUtc" TEXT NULL,
            CONSTRAINT "FK_Reviews_Products_ProductId" FOREIGN KEY ("ProductId")
                REFERENCES "Products" ("Id") ON DELETE RESTRICT,
            CONSTRAINT "FK_Reviews_Shops_ShopId" FOREIGN KEY ("ShopId")
                REFERENCES "Shops" ("Id") ON DELETE RESTRICT,
            CONSTRAINT "FK_Reviews_Users_UserId" FOREIGN KEY ("UserId")
                REFERENCES "Users" ("Id") ON DELETE SET NULL
        );
        CREATE INDEX IF NOT EXISTS "IX_Reviews_ProductId" ON "Reviews" ("ProductId");
        CREATE INDEX IF NOT EXISTS "IX_Reviews_ShopId" ON "Reviews" ("ShopId");
        CREATE INDEX IF NOT EXISTS "IX_Reviews_UserId" ON "Reviews" ("UserId");
        CREATE INDEX IF NOT EXISTS "IX_Reviews_ProductId_Status" ON "Reviews" ("ProductId", "Status");
        CREATE INDEX IF NOT EXISTS "IX_Reviews_Status" ON "Reviews" ("Status");
        CREATE INDEX IF NOT EXISTS "IX_Reviews_CreatedUtc" ON "Reviews" ("CreatedUtc");

        CREATE TABLE IF NOT EXISTS "ReviewVotes" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_ReviewVotes" PRIMARY KEY AUTOINCREMENT,
            "ReviewId" INTEGER NOT NULL,
            "UserId" TEXT NOT NULL,
            "IsHelpful" INTEGER NOT NULL,
            "CreatedUtc" TEXT NOT NULL,
            CONSTRAINT "FK_ReviewVotes_Reviews_ReviewId" FOREIGN KEY ("ReviewId")
                REFERENCES "Reviews" ("Id") ON DELETE CASCADE,
            CONSTRAINT "FK_ReviewVotes_Users_UserId" FOREIGN KEY ("UserId")
                REFERENCES "Users" ("Id") ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS "IX_ReviewVotes_ReviewId" ON "ReviewVotes" ("ReviewId");
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_ReviewVotes_ReviewId_UserId" ON "ReviewVotes" ("ReviewId", "UserId");
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
